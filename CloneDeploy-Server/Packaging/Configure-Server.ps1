# Runs elevated from Windows Installer. Reads UTF-8 JSON through standard input;
# credentials are never passed on process command lines.
[CmdletBinding()]
param([ValidateSet('Install', 'Rollback', 'Uninstall', 'Commit')][string]$Mode = 'Install')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ServerFirewall.ps1')
. (Join-Path $PSScriptRoot 'MulticastFirewall.ps1')
. (Join-Path $PSScriptRoot 'ServerSecrets.ps1')
$stateRoot = Join-Path $env:ProgramData 'CloneDeploy\ServerSetup'
$dataRoot = Join-Path $env:ProgramData 'CloneDeploy\Server'
$pgRoot = Join-Path $env:ProgramData 'CloneDeploy\PostgreSQL\18'
$stateFile = Join-Path $stateRoot 'installation.json'
$journalFile = Join-Path $stateRoot 'transaction.json'
$appService = 'CloneDeploy-Server'
$pgService = 'CloneDeploy-PostgreSQL'
$builderService = 'CloneDeploy WinPE Builder'
$script:dbRootCert = $null

function Read-SetupRequest {
    $reader = New-Object IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
    try { return $reader.ReadToEnd() | ConvertFrom-Json }
    catch { throw 'Unable to read the installer configuration.' }
    finally { $reader.Dispose() }
}

function Set-PrivateDirectory([string]$Path, [string[]]$ModifySids = @()) {
    if (Test-Path -LiteralPath $Path) {
        if ((Get-Item -LiteralPath $Path).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'An installation data directory must not be a reparse point.'
        }
    } else { [void][IO.Directory]::CreateDirectory($Path) }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($entry in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'))) {
        $sid = New-Object Security.Principal.SecurityIdentifier($entry[0])
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, $entry[1], 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    foreach ($value in $ModifySids) {
        $sid = New-Object Security.Principal.SecurityIdentifier($value)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
}

function New-ServerConfiguration([string]$ConnectionString, [string]$CertificateThumbprint, [string]$EncryptedDatabasePassword, [int]$HttpPort,
    [string]$TemplatePath = (Join-Path $PSScriptRoot 'server.template.json')) {
    $config = Get-Content -LiteralPath $TemplatePath -Raw | ConvertFrom-Json
    $config.ConnectionStrings.'CloneDeploy-Server-Database' = $ConnectionString
    $config.ServerSecrets.CertificateThumbprint = $CertificateThumbprint
    $config.ServerSecrets.DatabasePassword = $EncryptedDatabasePassword
    $config.Server.Listeners.HttpPort = $HttpPort
    return $config
}

function Quote-Argument([string]$Value) {
    # CommandLineToArgvW quoting; no shell is used.
    return '"' + ([regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1')) + '"'
}

function Invoke-Program([string]$File, [string[]]$Arguments, [string]$InputText = '', [hashtable]$Environment = @{}) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $File
    $info.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($key in $Environment.Keys) {
        if ($null -eq $Environment[$key]) { $info.EnvironmentVariables.Remove($key) }
        else { $info.EnvironmentVariables[$key] = $Environment[$key] }
    }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        # .NET Framework (Windows PowerShell 5.1) lacks StandardInputEncoding.
        $inputBytes = [Text.Encoding]::UTF8.GetBytes($InputText)
        $process.StandardInput.BaseStream.Write($inputBytes, 0, $inputBytes.Length)
        $process.StandardInput.Close()
        if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'A setup operation timed out.' }
        # Database errors can echo SQL containing passwords. Do not write child output to MSI logs.
        if ($process.ExitCode -ne 0) { throw "Setup operation failed: $([IO.Path]::GetFileName($File)) (exit $($process.ExitCode))." }
        # pg_ctl's server child can inherit the redirected handles after pg_ctl exits.
        # Only psql has a query result to consume; do not wait for a daemon's EOF.
        if ([IO.Path]::GetFileName($File) -ne 'psql.exe') { return '' }
        if (!$output.Wait(5000)) { throw 'Database command output did not complete.' }
        return $output.GetAwaiter().GetResult().Trim()
    } finally { $process.Dispose() }
}

function Remove-OwnedService([string]$Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') { Stop-Service -Name $Name -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
        [void](Invoke-Program "$env:SystemRoot\System32\sc.exe" @('delete', $Name))
    }
}

function Grant-ServiceAccountRights([string]$Sid) {
    if (!('CloneDeploy.Setup.AccountRights' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'AccountRights.cs')
    }
    [CloneDeploy.Setup.AccountRights]::Grant($Sid)
}

function Invoke-Database([string]$User, [string]$Password, [string]$Database, [string]$Sql) {
    if ($Password -match '[\r\n\x00]') { throw 'Database passwords cannot contain line breaks or null characters.' }
    $passFile = Join-Path $stateRoot ([guid]::NewGuid().ToString('N') + '.pgpass')
    $fields = @($script:dbHost, [string]$script:dbPort, $Database, $User, $Password)
    $line = ($fields | ForEach-Object { $_.Replace('\', '\\').Replace(':', '\:') }) -join ':'
    $environment = @{
        PGHOST = $script:dbHost; PGPORT = [string]$script:dbPort; PGUSER = $User
        PGPASSWORD = $null; PGPASSFILE = $passFile; PGDATABASE = $Database; PGCONNECT_TIMEOUT = '10'
        PGSSLMODE = $script:sslMode; PGCLIENTENCODING = 'UTF8'
        PGSSLROOTCERT = $script:dbRootCert
    }
    if ($User -eq 'postgres') { $environment.PGOPTIONS = '-c log_statement=none -c log_min_error_statement=panic' }
    try {
        [IO.File]::WriteAllText($passFile, $line, (New-Object Text.UTF8Encoding($false)))
        return Invoke-Program $script:psql @('-X', '--no-password', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1') $Sql $environment
    } finally { Remove-Item -LiteralPath $passFile -Force -ErrorAction SilentlyContinue }
}

try {
    if ($Mode -eq 'Commit') {
        Remove-Item -LiteralPath $journalFile -Force -ErrorAction SilentlyContinue
        exit 0
    }
    if ($Mode -eq 'Rollback') {
        if (Test-Path -LiteralPath $journalFile) {
            $journal = Get-Content -LiteralPath $journalFile -Raw | ConvertFrom-Json
            if ($journal.PSObject.Properties['CreatedMulticastRules']) {
                Remove-MulticastFirewall -Root ([IO.Path]::GetDirectoryName($journal.FirewallProgram)) -Role App -Names @($journal.CreatedMulticastRules)
            }
            if ($journal.PSObject.Properties['CreatedFirewallRules']) {
                Remove-ServerFirewall -Program $journal.FirewallProgram -Names @($journal.CreatedFirewallRules)
            }
            foreach ($name in $journal.CreatedServices) { Remove-OwnedService $name }
            # Initialized databases, credentials and account ownership are preserved for retry.
            Remove-Item -LiteralPath $journalFile -Force
        }
        exit 0
    }
    if ($Mode -eq 'Uninstall') {
        if (Test-Path -LiteralPath $stateFile) {
            $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
            if ($state.PSObject.Properties['BuilderInstalled'] -and $state.BuilderInstalled) { Remove-OwnedService $builderService }
            Remove-OwnedService $appService
            if ($state.DatabaseMode -eq 'Local') { Remove-OwnedService $pgService }
            Remove-ServerFirewall -Program (Join-Path $state.InstallPath 'CloneDeploy-Server.exe') -Names (Get-ServerFirewallNames)
            Remove-MulticastFirewall -Root $state.InstallPath -Role App -Names (Get-MulticastFirewallNames App)
        }
        # Keep the service account, data, keys and configuration so reinstall can reuse them.
        exit 0
    }

    Set-PrivateDirectory $stateRoot
    $request = Read-SetupRequest
    $request.ServiceUser = 'CloneDeploySvc'
    $installRoot = [IO.Path]::GetFullPath($request.InstallPath).TrimEnd('\')
    if (!(Test-Path -LiteralPath (Join-Path $installRoot 'CloneDeploy-Server.exe'))) { throw 'The application payload is incomplete.' }
    $builderExecutable = Join-Path $installRoot 'PeBuilder\CloneDeploy-WinPeBuilder.exe'
    if (!(Test-Path -LiteralPath $builderExecutable)) { throw 'The WinPE builder payload is missing.' }
    $script:psql = Join-Path $installRoot 'PostgreSQL\18\bin\psql.exe'
    $configFile = Join-Path $dataRoot 'server.json'
    $state = if (Test-Path -LiteralPath $stateFile) { Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json } else { $null }
    $journal = @{ CreatedServices = @(); CreatedFirewallRules = @(); CreatedMulticastRules = @(); FirewallProgram = (Join-Path $installRoot 'CloneDeploy-Server.exe') }
    Write-Json $journalFile $journal

    if ($state -and (Test-Path -LiteralPath $configFile) -and (Get-Service $appService -ErrorAction SilentlyContinue)) {
        if ($state.InstallPath -ne $installRoot) { throw 'Keep the existing installation directory when upgrading.' }
        $localUser = Get-LocalUser -Name $state.ServiceUser
        Grant-ServiceAccountRights $localUser.Sid.Value
        # Repair and upgrades retain credentials and database configuration.
        if ($state.DatabaseMode -eq 'Local') {
            if ((Get-Content -LiteralPath (Join-Path $pgRoot 'PG_VERSION') -Raw).Trim() -ne '18') { throw 'The existing database requires a separate major-version migration.' }
            Start-Service $pgService
        }
    } else {
        if ($request.ServiceUser -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,19}$') { throw 'Use a local account name of 1-20 letters, numbers, underscores or hyphens.' }
        if ($request.DatabaseMode -notin @('Local', 'Existing')) { throw 'Select a database installation option.' }
        if ($request.DatabaseMode -eq 'Local') {
            $request.DatabaseHost = '127.0.0.1'
            $request.DatabasePort = '5433'
            $request.DatabaseName = 'CloneDeploy-Server'
            $request.DatabaseUser = 'clonedeploy'
            $request.DatabasePassword = New-SetupPassword
        }
        $retained = $null
        if (Test-Path -LiteralPath $configFile) {
            if (!$state) { throw 'Server configuration exists without matching installation ownership.' }
            $retained = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
            $retainedConnection = New-Object System.Data.Common.DbConnectionStringBuilder
            $retainedConnection.ConnectionString = $retained.ConnectionStrings.'CloneDeploy-Server-Database'
            $retainedCertificate = Get-SetupCertificate $retained.ServerSecrets.CertificateThumbprint
            $request.DatabaseHost = [string]$retainedConnection['Host']
            $request.DatabasePort = [string]$retainedConnection['Port']
            $request.DatabaseName = [string]$retainedConnection['Database']
            $request.DatabaseUser = [string]$retainedConnection['Username']
            $request.DatabasePassword = Unprotect-SetupDatabasePassword $retained.ServerSecrets.DatabasePassword $retainedCertificate
        }
        if ([string]::IsNullOrWhiteSpace($request.DatabasePassword)) { throw 'Enter the external database password.' }
        if ($request.DatabaseName -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,62}$' -or $request.DatabaseUser -notmatch '^[A-Za-z][A-Za-z0-9_]{0,62}$') {
            throw 'Use simple database and login names (letters, numbers and underscores; database names may also contain hyphens).'
        }
        $script:dbHost = if ($request.DatabaseMode -eq 'Local') { '127.0.0.1' } else { [string]$request.DatabaseHost }
        $script:dbPort = 0
        if (![int]::TryParse($request.DatabasePort, [ref]$script:dbPort) -or $script:dbPort -lt 1 -or $script:dbPort -gt 65535) { throw 'Invalid PostgreSQL port.' }
        $script:sslMode = if ($request.DatabaseMode -eq 'Local') { 'disable' } else { 'verify-full' }
        if ($retained) {
            $request.HttpPort = [string]$retained.Server.Listeners.HttpPort
        }
        $webPort = 0
        if (![int]::TryParse($request.HttpPort, [ref]$webPort) -or $webPort -lt 1 -or $webPort -gt 65535) { throw 'Invalid HTTP port.' }
        if ($webPort -in @(443,8443,8444)) { throw 'HTTP setup/boot port must differ from management 443, service 8443 and portal 8444.' }
        if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains $webPort) {
            throw "HTTP port $webPort is already in use. Choose another HTTP port in setup or free the port before retrying."
        }
        if ($state -and ($state.DatabaseMode -ne $request.DatabaseMode -or $state.ServiceUser -ne $request.ServiceUser -or $state.InstallPath -ne $installRoot)) {
            throw 'Retained installation data exists. Reuse its installation path, database option and service account.'
        }
        $localUser = Get-LocalUser -Name $request.ServiceUser -ErrorAction SilentlyContinue
        if ($localUser -and (!$state -or $state.ServiceUser -ne $request.ServiceUser)) { throw 'CloneDeploySvc already exists and is not owned by this installation. Resolve the account conflict before retrying; setup will not modify an unrelated account.' }
        # Only the provisioning path needs a logon password. Never persist a copy.
        # Normal repair/upgrade above preserves the password already held by SCM.
        $servicePassword = if ([string]::IsNullOrEmpty($request.ServicePassword)) { New-SetupPassword } else { [string]$request.ServicePassword }
        if ($localUser -and (Get-Service $appService -ErrorAction SilentlyContinue)) { throw 'An incomplete installation still has an application service. Repair its retained state before retrying.' }
        try {
            if (!$localUser) {
                $localUser = New-LocalUser -Name $request.ServiceUser -Password (ConvertTo-SecureString $servicePassword -AsPlainText -Force) -Description 'CloneDeploy Server service account' -PasswordNeverExpires -UserMayNotChangePassword
            } else {
                Set-LocalUser -Name $request.ServiceUser -Password (ConvertTo-SecureString $servicePassword -AsPlainText -Force)
            }
        } catch {
            throw 'Unable to create or update the CloneDeploySvc account. Check local account/password policies and permissions. If the generated password was rejected, rerun setup with Specify service account password selected.'
        }
        if (!$state) {
            $state = [pscustomobject]@{ InstallPath = $installRoot; ServiceUser = $request.ServiceUser; DatabaseMode = $request.DatabaseMode; DatabasePort = $script:dbPort }
            Write-Json $stateFile $state
        }
        $usersGroup = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
        if (!(Get-LocalGroupMember -SID $usersGroup | Where-Object { $_.SID -eq $localUser.Sid })) {
            Add-LocalGroupMember -SID $usersGroup -Member $localUser
        }
        $account = "$env:COMPUTERNAME\$($request.ServiceUser)"
        Grant-ServiceAccountRights $localUser.Sid.Value
        Set-PrivateDirectory $dataRoot @($localUser.Sid.Value)
        if (!$state.PSObject.Properties['ProtectionCertificateThumbprint']) {
            if ($retained) { throw 'The retained server configuration has no matching certificate ownership.' }
            $certificate = New-SelfSignedCertificate -Type DocumentEncryptionCert -Subject 'CN=CloneDeploy Server Secret Protection' -FriendlyName 'CloneDeploy Server Secret Protection' -CertStoreLocation 'Cert:\LocalMachine\My' -Provider 'Microsoft Software Key Storage Provider' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(15)
            $state | Add-Member -NotePropertyName ProtectionCertificateThumbprint -NotePropertyValue $certificate.Thumbprint
            Write-Json $stateFile $state
        }
        $certificate = Get-SetupCertificate $state.ProtectionCertificateThumbprint
        Grant-SetupCertificateAccess $certificate $localUser.Sid.Value
        if ($request.DatabaseMode -eq 'Existing') {
            $script:dbRootCert = 'system'
            if ($retained -and $retainedConnection.ContainsKey('Root Certificate')) {
                $script:dbRootCert = [string]$retainedConnection['Root Certificate']
            } elseif (![string]::IsNullOrWhiteSpace($request.DatabaseCa)) {
                $script:dbRootCert = Join-Path $dataRoot 'database-ca.pem'
                Copy-Item -LiteralPath $request.DatabaseCa -Destination $script:dbRootCert -Force
            }
            # Reject incorrect external credentials before retaining configuration,
            # so a failed first attempt can be corrected in the installer.
            $check = Invoke-Database $request.DatabaseUser $request.DatabasePassword $request.DatabaseName "SELECT CASE WHEN has_database_privilege(current_database(), 'CREATE') AND EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citext') THEN 'ready' ELSE 'missing' END;"
            if ($check -ne 'ready') { throw 'The database login needs schema migration permissions and the database must contain the citext extension.' }
        }
        # Retain the encrypted application credential before database provisioning so
        # retries reuse the same login password even after a partial setup failure.
        if (!$retained) {
            $connection = New-Object System.Data.Common.DbConnectionStringBuilder
            $connection['Host'] = $script:dbHost; $connection['Port'] = [string]$script:dbPort
            $connection['Database'] = $request.DatabaseName; $connection['Username'] = $request.DatabaseUser
            $connection['SSL Mode'] = if ($request.DatabaseMode -eq 'Local') { 'Disable' } else { 'VerifyFull' }
            if ($script:dbRootCert -and $script:dbRootCert -ne 'system') { $connection['Root Certificate'] = $script:dbRootCert }
            Write-Json $configFile (New-ServerConfiguration $connection.ConnectionString $certificate.Thumbprint (Protect-SetupDatabasePassword $request.DatabasePassword $certificate) $webPort)
        } elseif ($retained.ServerSecrets.CertificateThumbprint -ne $certificate.Thumbprint) {
            throw 'The retained configuration and installer encryption certificates do not match.'
        }
        if ($request.DatabaseMode -eq 'Local') {
            if ($state.DatabasePort -ne $script:dbPort) { throw 'Use the retained database port.' }
            $pgCtl = Join-Path $installRoot 'PostgreSQL\18\bin\pg_ctl.exe'
            if (!(Get-Service $pgService -ErrorAction SilentlyContinue)) {
                if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains $script:dbPort) {
                    throw 'The PostgreSQL port is already in use. Choose a different port.'
                }
                $journal.CreatedServices += $pgService
                Write-Json $journalFile $journal
                [void](Invoke-Program $pgCtl @('register', '-N', $pgService, '-D', $pgRoot, '-U', "NT SERVICE\$pgService", '-S', 'auto'))
            }
            $pgSid = (New-Object Security.Principal.NTAccount("NT SERVICE\$pgService")).Translate([Security.Principal.SecurityIdentifier]).Value
            Set-PrivateDirectory $pgRoot @($pgSid)
            $adminPasswordFile = Join-Path $stateRoot 'postgres-password.txt'
            $protectedAdminFile = Join-Path $stateRoot 'postgres-password.dpapi'
            if (Test-Path -LiteralPath $protectedAdminFile) {
                Add-Type -AssemblyName System.Security
                $adminPassword = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($protectedAdminFile), $null, 'LocalMachine'))
            } else {
                if (Test-Path -LiteralPath (Join-Path $pgRoot 'PG_VERSION')) { throw 'An existing PostgreSQL cluster has no matching installer credentials. It will not be modified.' }
                $random = New-Object byte[] 48
                $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
                try { $rng.GetBytes($random) } finally { $rng.Dispose() }
                $adminPassword = [Convert]::ToBase64String($random)
                Add-Type -AssemblyName System.Security
                [IO.File]::WriteAllBytes($protectedAdminFile, [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($adminPassword), $null, 'LocalMachine'))
            }
            if (!(Test-Path -LiteralPath (Join-Path $pgRoot 'PG_VERSION'))) {
                if (@(Get-ChildItem -LiteralPath $pgRoot -Force).Count) { throw 'The database directory is not empty. Setup will not overwrite a partially initialized cluster.' }
                try {
                    [IO.File]::WriteAllText($adminPasswordFile, $adminPassword, (New-Object Text.UTF8Encoding($false)))
                    [void](Invoke-Program $pgCtl @('initdb', '-D', $pgRoot, '-o', ('--username=postgres --encoding=UTF8 --locale=C --auth=scram-sha-256 --pwfile=' + (Quote-Argument $adminPasswordFile))))
                } finally { Remove-Item -LiteralPath $adminPasswordFile -Force -ErrorAction SilentlyContinue }
                [IO.File]::AppendAllText((Join-Path $pgRoot 'postgresql.conf'), "`nlisten_addresses = '127.0.0.1'`nport = $script:dbPort`npassword_encryption = 'scram-sha-256'`nlogging_collector = on`nlog_filename = 'postgresql-%a.log'`nlog_truncate_on_rotation = on`nlog_rotation_age = '1d'`n")
            }
            if ((Get-Content -LiteralPath (Join-Path $pgRoot 'PG_VERSION') -Raw).Trim() -ne '18') { throw 'A PostgreSQL major upgrade requires a separate database migration.' }
            Set-PrivateDirectory $pgRoot @($pgSid)
            Start-Service $pgService
            (Get-Service $pgService).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
            $database = [string]$request.DatabaseName
            $login = [string]$request.DatabaseUser
            $passwordLiteral = ([string]$request.DatabasePassword).Replace("'", "''")
            $exists = Invoke-Database 'postgres' $adminPassword 'postgres' "SELECT 1 FROM pg_roles WHERE rolname = '$login';"
            if ($exists -ne '1') { [void](Invoke-Database 'postgres' $adminPassword 'postgres' "CREATE ROLE `"$login`" LOGIN PASSWORD '$passwordLiteral' NOSUPERUSER NOCREATEDB NOCREATEROLE;") }
            $exists = Invoke-Database 'postgres' $adminPassword 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$database';"
            if ($exists -ne '1') { [void](Invoke-Database 'postgres' $adminPassword 'postgres' "CREATE DATABASE `"$database`" OWNER `"$login`";") }
            [void](Invoke-Database 'postgres' $adminPassword $database 'CREATE EXTENSION IF NOT EXISTS citext;')
        }

        if ($request.DatabaseMode -eq 'Local') {
            $check = Invoke-Database $request.DatabaseUser $request.DatabasePassword $request.DatabaseName "SELECT CASE WHEN has_database_privilege(current_database(), 'CREATE') AND EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citext') THEN 'ready' ELSE 'missing' END;"
            if ($check -ne 'ready') { throw 'The database login needs schema migration permissions and the database must contain the citext extension.' }
        }
        if (!(Get-Service $appService -ErrorAction SilentlyContinue)) {
            $journal.CreatedServices += $appService
            Write-Json $journalFile $journal
            $credential = New-Object Management.Automation.PSCredential($account, (ConvertTo-SecureString $servicePassword -AsPlainText -Force))
            $parameters = @{ Name = $appService; BinaryPathName = (Quote-Argument (Join-Path $installRoot 'CloneDeploy-Server.exe')); DisplayName = 'CloneDeploy Server'; StartupType = 'Automatic'; Credential = $credential }
            if ($request.DatabaseMode -eq 'Local') { $parameters.DependsOn = @($pgService) }
            New-Service @parameters | Out-Null
        }
    }
    $configuration = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    Set-PrivateDirectory $dataRoot @($localUser.Sid.Value)
    Set-PrivateDirectory (Join-Path $dataRoot 'keys') @($localUser.Sid.Value)
    if ($configuration.ServerSecrets.CertificateThumbprint -ne $state.ProtectionCertificateThumbprint) { throw 'The server encryption certificate does not match installation ownership.' }
    $certificate = Get-SetupCertificate $configuration.ServerSecrets.CertificateThumbprint
    Grant-SetupCertificateAccess $certificate $localUser.Sid.Value
    Install-ServerFirewall -Program $journal.FirewallProgram -WebPorts (Get-ServerWebPorts $configuration) -BeforeCreate {
        param($name)
        $journal.CreatedFirewallRules += $name
        Write-Json $journalFile $journal
    }
    Install-MulticastFirewall -Root $installRoot -Role App -BeforeCreate {
        param($name)
        $journal.CreatedMulticastRules += $name
        Write-Json $journalFile $journal
    }
    $builderRoot = Join-Path $dataRoot 'Staging\WinPe'
    $builderRoot = [IO.Path]::GetFullPath($builderRoot)
    if (!$builderRoot.StartsWith($dataRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installer-managed WinPE work folder must be a subfolder of the server data directory.'
    }
    Set-PrivateDirectory $builderRoot @($localUser.Sid.Value)
    $builderCommand = (Quote-Argument $builderExecutable) + ' --WinPe:RootPath ' + (Quote-Argument $builderRoot) + ' --WinPe:QueueWriterSid ' + (Quote-Argument $localUser.Sid.Value)
    $existingBuilder = Get-CimInstance Win32_Service -Filter "Name='$builderService'"
    if ($existingBuilder) {
        if (($existingBuilder.PathName -ne $builderCommand) -or $existingBuilder.StartName -ne 'LocalSystem') {
            throw 'A WinPE builder service exists with a different executable, work folder or identity. Setup will not replace it.'
        }
    } else {
        $journal.CreatedServices += $builderService
        Write-Json $journalFile $journal
        New-Service -Name $builderService -BinaryPathName $builderCommand -DisplayName $builderService -StartupType Automatic -Description 'Builds CloneDeploy WinPE images and manages ADK prerequisites.' | Out-Null
    }
    $state | Add-Member -NotePropertyName BuilderInstalled -NotePropertyValue $true -Force
    Write-Json $stateFile $state
    New-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$appService" -Name Environment -PropertyType MultiString -Value @('ASPNETCORE_ENVIRONMENT=Production') -Force | Out-Null
    Start-Service $builderService
    (Get-Service $builderService).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    Start-Service $appService
    (Get-Service $appService).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    $configuration = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    $listenPort = [int]$configuration.Server.Listeners.HttpPort
    $ready = $false
    $deadline = (Get-Date).AddMinutes(3)
    do {
        if ((Get-Service $appService).Status -ne 'Running') { throw 'The server stopped during startup. Check the application log in ProgramData\CloneDeploy\Server\logs.' }
        $connection = New-Object Net.Sockets.TcpClient
        try {
            $pending = $connection.ConnectAsync('127.0.0.1', $listenPort)
            $ready = $pending.Wait(1000) -and $connection.Connected
        } catch { $ready = $false }
        finally { $connection.Dispose() }
        if (!$ready) { Start-Sleep -Seconds 1 }
    } until ($ready -or (Get-Date) -gt $deadline)
    if (!$ready) { throw 'The server did not finish its database migrations and open its web listener in time. Check the application log.' }
    if ((Get-Service $builderService).Status -ne 'Running') { throw 'The WinPE builder stopped during startup. Check the Windows Application event log.' }
    exit 0
} catch {
    # Do not serialize request objects, invocation lines, native stderr, or passwords.
    $message = "CloneDeploy server setup failed during $Mode. $($_.Exception.Message)"
    if (Test-Path -LiteralPath $stateRoot) { [IO.File]::AppendAllText((Join-Path $stateRoot 'setup.log'), "$message`r`n") }
    Write-Host $message
    exit 1
}
