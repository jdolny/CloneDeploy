$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } elseif ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
$apps = Join-Path $env:ProgramFiles 'WindowsApps'
$originalPath = $env:PATH

function Find-Package([string]$name, [version]$minimum = '0.0.0.0') {
    if (!(Test-Path -LiteralPath $apps)) { return }
    Get-ChildItem -LiteralPath $apps -Directory -Filter ($name + '_*__8wekyb3d8bbwe') | ForEach-Object {
        $parts = $_.Name.Split('_')
        if ($parts.Length -ge 5 -and $parts[2] -in @($arch, 'neutral')) {
            $version = $null
            if ([version]::TryParse($parts[1], [ref]$version) -and $version -ge $minimum) {
                [pscustomobject]@{ Location = $_.FullName; Version = $version }
            }
        }
    } | Sort-Object Version -Descending | Select-Object -First 1
}
function Resolve-WinGet {
    $package = Find-Package 'Microsoft.DesktopAppInstaller'
    if (!$package) { return }
    $exe = Join-Path $package.Location 'winget.exe'
    if (!(Test-Path -LiteralPath $exe)) { return }
    $paths = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($package.Location)
    while ($pending.Count -gt 0) {
        $location = $pending.Dequeue()
        if ($paths.Contains($location)) { continue }
        $paths.Add($location)
        [xml]$manifest = Get-Content -LiteralPath (Join-Path $location 'AppxManifest.xml') -Raw
        foreach ($dependency in $manifest.SelectNodes("//*[local-name()='Dependencies']/*[local-name()='PackageDependency']")) {
            $framework = Find-Package $dependency.Name ([version]$dependency.MinVersion)
            if ($framework) { $pending.Enqueue($framework.Location) }
        }
    }
    [pscustomobject]@{ FileName = $exe; Directories = $paths.ToArray() }
}
function Test-WinGet($resolved) {
    if (!$resolved) { return $false }
    $env:PATH = ($resolved.Directories -join ';') + ';' + $originalPath
    $probe = New-Object Diagnostics.Process
    $probe.StartInfo.FileName = $resolved.FileName
    $probe.StartInfo.Arguments = '--version'
    $probe.StartInfo.UseShellExecute = $false
    $probe.StartInfo.CreateNoWindow = $true
    $probe.StartInfo.RedirectStandardOutput = $true
    $probe.StartInfo.RedirectStandardError = $true
    try {
        [void]$probe.Start()
        $stdout = $probe.StandardOutput.ReadToEndAsync()
        $stderr = $probe.StandardError.ReadToEndAsync()
        if (!$probe.WaitForExit(30000)) { $probe.Kill(); throw 'WinGet startup timed out.' }
        return $probe.ExitCode -eq 0
    } finally { $probe.Dispose() }
}
function Download-Asset($release, [string]$name, [string]$destination) {
    $asset = @($release.assets | Where-Object name -eq $name)
    if ($asset.Count -ne 1 -or $asset[0].browser_download_url -notlike 'https://github.com/microsoft/winget-cli/releases/download/*') {
        throw "The official WinGet release is missing $name."
    }
    Invoke-WebRequest -UseBasicParsing -Uri $asset[0].browser_download_url -OutFile $destination -TimeoutSec 180
    if ($asset[0].digest -like 'sha256:*') {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $asset[0].digest.Substring(7)) { throw "Hash mismatch for $name." }
    }
}

$stage = $null
try {
    if (![Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { throw 'WinGet machine preparation must run as SYSTEM.' }
    $resolved = Resolve-WinGet
    if (!(Test-WinGet $resolved)) {
        $stage = Join-Path ([IO.Path]::GetTempPath()) ('CloneDeploy-WinGet-' + [guid]::NewGuid().ToString('N'))
        $directory = [IO.Directory]::CreateDirectory($stage)
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
                (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        }
        $directory.SetAccessControl($acl)
        $runtime = Join-Path $stage 'VC_redist.exe'
        Invoke-WebRequest -UseBasicParsing -Uri "https://aka.ms/vs/17/release/vc_redist.$arch.exe" -OutFile $runtime -TimeoutSec 180
        $signature = Get-AuthenticodeSignature -LiteralPath $runtime
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'Visual C++ runtime signature validation failed.' }
        $installer = Start-Process -FilePath $runtime -ArgumentList '/install /quiet /norestart' -WindowStyle Hidden -Wait -PassThru
        if ($installer.ExitCode -notin @(0, 1638, 3010)) { throw "Visual C++ runtime installation failed: $($installer.ExitCode)." }
        # A desktop runtime repair alone may be sufficient. Do not update a now-healthy App Installer.
        $resolved = Resolve-WinGet
        if (!(Test-WinGet $resolved)) {
            $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/microsoft/winget-cli/releases/latest' -Headers @{ 'User-Agent' = 'CloneDeploy-Agent' } -TimeoutSec 60
            $bundle = Join-Path $stage 'AppInstaller.msixbundle'
            $zip = Join-Path $stage 'Dependencies.zip'
            Download-Asset $release 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle' $bundle
            Download-Asset $release 'DesktopAppInstaller_Dependencies.zip' $zip
            $licenseAsset = @($release.assets | Where-Object name -like '*License1.xml')
            if ($licenseAsset.Count -ne 1) { throw 'The official WinGet license asset is missing.' }
            $license = Join-Path $stage 'License.xml'
            Download-Asset $release $licenseAsset[0].name $license
            $expanded = Join-Path $stage 'Dependencies'
            Expand-Archive -LiteralPath $zip -DestinationPath $expanded
            $dependencies = @(Get-ChildItem -LiteralPath (Join-Path $expanded $arch) -File | Where-Object Extension -in @('.appx', '.msix') | Select-Object -ExpandProperty FullName)
            if ($dependencies.Count -eq 0) { throw "No WinGet dependencies found for $arch." }
            # Windows validates package signatures and dependency versions during provisioning.
            Add-AppxProvisionedPackage -Online -PackagePath $bundle -DependencyPackagePath $dependencies -LicensePath $license | Out-Null
            $resolved = Resolve-WinGet
            if (!(Test-WinGet $resolved)) { throw 'WinGet still cannot start after prerequisite installation. Check the App Installer deployment logs.' }
        }
    }
    $resolved | ConvertTo-Json -Compress
} catch {
    [Console]::Error.WriteLine('WinGet prerequisite preparation failed: ' + $_.Exception.Message)
    exit 1
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        $expected = [IO.Path]::GetFullPath($stage)
        $actual = (Get-Item -LiteralPath $stage).FullName
        if ($actual -eq $expected -and [IO.Path]::GetFileName($actual) -like 'CloneDeploy-WinGet-*' -and
            [IO.Path]::GetDirectoryName($actual).TrimEnd('\') -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
            !((Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $actual -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
