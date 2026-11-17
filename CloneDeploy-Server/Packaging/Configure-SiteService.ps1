# Elevated MSI lifecycle action. Enrollment contents never enter MSI properties or command lines.
[CmdletBinding()]
param([ValidateSet('Install','Rollback','Commit','Uninstall')][string]$Mode = 'Install')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$serviceName = 'CloneDeploy-SiteService'
$stateRoot = Join-Path $env:ProgramData 'CloneDeploy\SiteServiceSetup'
$dataRoot = Join-Path $env:ProgramData 'CloneDeploy\SiteService'
$stateFile = Join-Path $stateRoot 'installation.json'
$journalFile = Join-Path $stateRoot 'transaction.json'
$worker = $false
$serviceUser = 'CloneDeploySiteSvc'
$serviceIdentity = ".\$serviceUser"

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
}
function Assert-RegularDirectory([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        # ProgramData is hidden. Inspect it without treating hidden ancestors as missing.
        if ((Test-Path -LiteralPath $current) -and (Get-Item -LiteralPath $current -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'SiteService setup directories must not use reparse points.'
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Protect-Directory([string]$Path) {
    Assert-RegularDirectory $Path
    [void][IO.Directory]::CreateDirectory($Path)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($value in @('S-1-5-18','S-1-5-32-544')) {
        $sid = New-Object Security.Principal.SecurityIdentifier($value)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Owned-Service {
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($service -and ($service.PathName -ne $script:command -or $service.StartName -ne $serviceIdentity)) {
        throw 'A SiteService service exists with a different executable or identity. Setup will not modify it.'
    }
    return $service
}
function Remove-OwnedService {
    if (Owned-Service) {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
        & "$env:SystemRoot\System32\sc.exe" delete $serviceName | Out-Null
        if ($LASTEXITCODE) { throw 'Could not remove the SiteService service registration.' }
    }
}

try {
    $reader = New-Object IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
    try { $request = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $worker = $request.InstallRole -eq 'WindowsWorker'
    if ($worker) {
        $serviceName = 'CloneDeploy-WindowsWorker'; $serviceIdentity = 'LocalSystem'
        $stateRoot = Join-Path $env:ProgramData 'CloneDeploy\WindowsWorkerSetup'
        $dataRoot = Join-Path $env:ProgramData 'CloneDeploy-WindowsWorker'
        $stateFile = Join-Path $stateRoot 'installation.json'; $journalFile = Join-Path $stateRoot 'transaction.json'
    } else {
        . (Join-Path $PSScriptRoot 'MulticastFirewall.ps1')
        . (Join-Path $PSScriptRoot 'SiteServiceFirewall.ps1')
    }
    if ($Mode -eq 'Install') {
        $other = if ($worker) { 'SiteService' } else { 'WindowsWorker' }
        if ((Get-Service -Name "CloneDeploy-$other" -ErrorAction SilentlyContinue) -or
            (Test-Path -LiteralPath "HKLM:\SOFTWARE\CloneDeploy\Installation\$other")) {
            throw 'Site Service and Windows Worker cannot be installed on the same host. Uninstall the other component first.'
        }
    }
    $installRoot = [IO.Path]::GetFullPath([string]$request.InstallPath).TrimEnd('\')
    $component = if ($worker) { 'WindowsWorker' } else { 'SiteService' }
    $executable = Join-Path $installRoot "$component\CloneDeploy-$component.exe"
    $script:command = '"' + $executable + '"'
    Assert-RegularDirectory $installRoot
    Protect-Directory $stateRoot
    Assert-RegularDirectory $dataRoot
    $state = if (Test-Path -LiteralPath $stateFile) { Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json } else { $null }
    if ($state -and $state.InstallPath -ne $installRoot) { throw 'Keep the original SiteService installation directory for repair or reinstallation.' }

    if ($Mode -eq 'Commit') {
        Remove-Item -LiteralPath $journalFile -Force -ErrorAction SilentlyContinue
        exit 0
    }
    if ($Mode -eq 'Uninstall') {
        if ($state) { Remove-OwnedService }
        if ($state -and !$worker) { Remove-SiteServiceFirewall -Program $executable -Names (Get-SiteServiceFirewallNames) }
        if ($state -and !$worker) { Remove-MulticastFirewall -Root (Join-Path $installRoot 'SiteService') -Role SiteService -Names (Get-MulticastFirewallNames SiteService) }
        # Preserve identity, certificates and ownership for reinstall. Revoke access in the main app when retiring this machine.
        exit 0
    }
    if ($Mode -eq 'Rollback') {
        if (Test-Path -LiteralPath $journalFile) {
            $journal = Get-Content -LiteralPath $journalFile -Raw | ConvertFrom-Json
            if ($journal.InstallPath -ne $installRoot) { throw 'Unexpected SiteService transaction ownership.' }
            if (!$worker -and $journal.PSObject.Properties['CreatedFirewallRules']) {
                Remove-SiteServiceFirewall -Program $executable -Names @($journal.CreatedFirewallRules)
            }
            if (!$worker -and $journal.PSObject.Properties['CreatedMulticastRules']) {
                Remove-MulticastFirewall -Root (Join-Path $installRoot 'SiteService') -Role SiteService -Names @($journal.CreatedMulticastRules)
            }
            if ($journal.CreatedService) { Remove-OwnedService }
            Remove-Item -LiteralPath $journalFile -Force
        }
        # Keep locally generated keys so retry cannot lose an already redeemed identity.
        exit 0
    }
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The SiteService executable is missing.' }
    $existing = Owned-Service
    if ($existing -and !$state) { throw 'An unmanaged SiteService installation already exists. Setup will not take ownership.' }
    $protectedState = Join-Path $dataRoot 'enrollment.protected'
    if ((Test-Path -LiteralPath $protectedState) -and !$state) { throw 'Unmanaged enrollment state already exists. Setup will not replace it.' }
    $journal = @{ InstallPath = $installRoot; CreatedService = $false; CreatedMulticastRules = @(); CreatedFirewallRules = @() }
    Write-Json $journalFile $journal
    if (!(Test-Path -LiteralPath $protectedState)) {
        $file = [string]$request.EnrollmentFile
        if (!$file -or ![IO.Path]::IsPathRooted($file) -or $file.StartsWith('\\') -or !(Test-Path -LiteralPath $file -PathType Leaf)) {
            throw 'Select a local SiteService setup JSON file exported from Admin Settings > Site Services.'
        }
        if ((Get-Item -LiteralPath $file).Length -gt 32768) { throw 'The setup JSON file is too large.' }
        if ($file.Contains('"')) { throw 'Invalid setup file path.' }
        # Claim state before import so a failed install can safely retain and reuse its keys.
        Write-Json $stateFile @{ InstallPath = $installRoot }
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $executable
        $info.Arguments = '--import-enrollment "' + $file + '"'
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        if ($worker) { $info.EnvironmentVariables['CLONEDEPLOY_WORKER_DATA'] = $dataRoot }
        $info.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Production'
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $info
        try {
            [void]$process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'SiteService setup import timed out.' }
            if ($process.ExitCode) { throw 'SiteService could not import the JSON. Check that it is valid, unexpired, and contains the main server IP.' }
            # Only the public fingerprint is persisted; never log arbitrary child output or JSON contents.
            $match = [regex]::Match($stdout.GetAwaiter().GetResult(), 'Identity key fingerprint: ([A-F0-9]{64})')
            if ($match.Success) { [IO.File]::WriteAllText((Join-Path $stateRoot 'identity-fingerprint.txt'), $match.Value) }
        } finally { $process.Dispose() }
    }
    Write-Json $stateFile @{ InstallPath = $installRoot }
    $credential = $null
    if (!$worker) {
        # The installer owns this non-administrator account; credentials never enter MSI properties or command lines.
        $secretPath = Join-Path $stateRoot 'service-password.protected'
        $accountPath = Join-Path $stateRoot 'service-account.json'
        Add-Type -AssemblyName System.Security
        $localUser = Get-LocalUser -Name $serviceUser -ErrorAction SilentlyContinue
        if ($localUser) {
            if (!(Test-Path -LiteralPath $accountPath) -or !(Test-Path -LiteralPath $secretPath)) { throw 'An unmanaged Site Service account already exists.' }
            $owned = Get-Content -LiteralPath $accountPath -Raw | ConvertFrom-Json
            if ($owned.Sid -ne $localUser.Sid.Value) { throw 'Site Service account ownership changed.' }
            $password = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($secretPath), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine))
        } else {
            $random = New-Object byte[] 48
            $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
            try { $rng.GetBytes($random) } finally { $rng.Dispose() }
            $password = [Convert]::ToBase64String($random) + 'aA1!'
            [IO.File]::WriteAllBytes($secretPath, [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($password), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine))
            $localUser = New-LocalUser -Name $serviceUser -Password (ConvertTo-SecureString $password -AsPlainText -Force) -Description 'CloneDeploy Site Service' -PasswordNeverExpires -UserMayNotChangePassword
            Write-Json $accountPath @{ Sid = $localUser.Sid.Value }
        }
        $administrators = Get-LocalGroup -SID 'S-1-5-32-544'
        if (Get-LocalGroupMember -Group $administrators | Where-Object { $_.SID -eq $localUser.Sid }) { throw 'Site Service account must not be an administrator.' }
        if (!('CloneDeploy.Setup.AccountRights' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'AccountRights.cs') }
        [CloneDeploy.Setup.AccountRights]::GrantServiceOnly($localUser.Sid.Value)
        $acl = Get-Acl -LiteralPath $dataRoot
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($localUser.Sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        Set-Acl -LiteralPath $dataRoot -AclObject $acl
        $binaryAcl = Get-Acl -LiteralPath $installRoot
        $binaryAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($localUser.Sid, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        Set-Acl -LiteralPath $installRoot -AclObject $binaryAcl
        $credential = New-Object Management.Automation.PSCredential($serviceIdentity, (ConvertTo-SecureString $password -AsPlainText -Force))
        if ($existing) {
            Stop-Service -Name $serviceName -Force
            $changed = Invoke-CimMethod -InputObject $existing -MethodName Change -Arguments @{ StartName = $serviceIdentity; StartPassword = $password }
            if ($changed.ReturnValue -ne 0) { throw 'Unable to change Site Service account.' }
        }
        $password = $null
    }
    if (!$existing) {
        $journal.CreatedService = $true
        Write-Json $journalFile $journal
        $parameters = @{ Name = $serviceName; BinaryPathName = $script:command; DisplayName = "CloneDeploy $component"; StartupType = 'Automatic'; Description = 'Approve enrollment in the main application.' }
        if ($credential) { $parameters.Credential = $credential }
        New-Service @parameters | Out-Null
    }
    if (!$worker) {
        $configurationFile = Join-Path $dataRoot 'siteservice.json'
        if (!(Test-Path -LiteralPath $configurationFile)) {
            Copy-Item -LiteralPath (Join-Path $installRoot 'SiteService\siteservice.example.json') -Destination $configurationFile
        }
    }
    $serviceEnvironment = @('DOTNET_ENVIRONMENT=Production')
    if ($worker) { $serviceEnvironment += "CLONEDEPLOY_WORKER_DATA=$dataRoot" }
    New-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null
    if (!$worker) {
    Install-SiteServiceFirewall -Program $executable -BeforeCreate {
        param($name)
        $journal.CreatedFirewallRules += $name
        Write-Json $journalFile $journal
    }
    Install-MulticastFirewall -Root (Join-Path $installRoot 'SiteService') -Role SiteService -BeforeCreate {
        param($name)
        $journal.CreatedMulticastRules += $name
        Write-Json $journalFile $journal
    }
    Start-Service $serviceName
    }
    if ($worker) { Start-Service $serviceName }
    (Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    # Approval and server reachability are asynchronous; installation never waits for web approval.
    exit 0
} catch {
    # Do not include exception text: file parsing and native errors may contain enrollment data.
    # Emit metadata first: the private directory may not exist or may be inaccessible.
    [Console]::Error.WriteLine(('CDSETUP_DIAGNOSTIC phase=script line={0} category={1} hresult={2}' -f $_.InvocationInfo.ScriptLineNumber,[int]$_.CategoryInfo.Category,$_.Exception.HResult))
    if (Test-Path -LiteralPath $stateRoot) {
        $errorType = $_.Exception.GetType().FullName
        $errorLine = $_.InvocationInfo.ScriptLineNumber
        [IO.File]::AppendAllText((Join-Path $stateRoot 'setup.log'), "SiteService setup failed during $Mode. Exception type: $errorType; script line: $errorLine. Check the selected JSON, directory ownership and service configuration. No enrollment contents were logged.`r`n")
    }
    exit 1
}
