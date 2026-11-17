[CmdletBinding()]
param(
    [string]$Version = '1',
    [ValidateSet('App', 'SiteService', 'WindowsWorker')][string]$Role = 'App',
    [string]$PostgreSqlArchive,
    [string]$AdvancedInstaller = (Join-Path ${env:ProgramFiles(x86)} 'Caphyon\Advanced Installer 15.1\bin\x86\AdvancedInstaller.com'),
    [switch]$SkipPublish,
    [switch]$SkipSigning,
    [string]$SignTool = 'C:\Microsoft Trusted Signing\Microsoft.Windows.SDK.BuildTools\bin\10.0.22621.0\x64\signtool.exe',
    [string]$SigningDll = 'C:\Microsoft Trusted Signing\Microsoft.Trusted.Signing.Client\bin\x64\Azure.CodeSigning.Dlib.dll',
    [string]$SigningProfile = 'C:\Microsoft Trusted Signing\profile.json'
)
$ErrorActionPreference = 'Stop'
# Use the same Telos version validation as the agents and release planner.
$Version = (& (Join-Path $PSScriptRoot '../../CloneDeploy-Agent.Win/Packaging/Get-AgentReleaseVersion.ps1') -Version $Version).MsiVersion
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$signing = @{ SignTool = $SignTool; SigningDll = $SigningDll; SigningProfile = $SigningProfile }
if (!$SkipSigning) {
    foreach ($required in $signing.Values) {
        if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Signing input is missing: $required" }
    }
}
$output = Join-Path $repo $(if ($Role -eq 'App') { 'artifacts/server-installer' } elseif ($Role -eq 'WindowsWorker') { 'artifacts/windowsworker-installer' } else { 'artifacts/siteservice-installer' })
    $payload = Join-Path $repo 'artifacts/server-installer/payload'
    $packageName = if ($Role -eq 'App') { 'CloneDeploy-Server' } else { "CloneDeploy-$Role" }
    if (!(Test-Path -LiteralPath $AdvancedInstaller)) { throw 'Advanced Installer is required. Supply -AdvancedInstaller with its executable path.' }
    $PostgreSqlArchive = & (Join-Path $PSScriptRoot 'Resolve-PostgreSqlArchive.ps1') -ArchivePath $PostgreSqlArchive
    [void][IO.Directory]::CreateDirectory($output)
$packageStage = Join-Path $output ('package-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($packageStage)
& dotnet build (Join-Path $PSScriptRoot 'CustomActions/CustomActions.csproj') -c Release "-p:Version=$Version"
if ($LASTEXITCODE) { throw 'Installer custom action build failed.' }
$customActionPath = Join-Path $PSScriptRoot 'CustomActions/bin/Release/net462/CloneDeploy.Server.Setup.CA.dll'
$customActionBytes = [IO.File]::ReadAllBytes($customActionPath)
$peOffset = [BitConverter]::ToInt32($customActionBytes, 60)
if ([BitConverter]::ToUInt16($customActionBytes, $peOffset + 4) -ne 0x8664) { throw 'The installer custom action native wrapper must be x64.' }
if (!$SkipPublish) {
    # CSS scopes depend on the assembly name, but processed CSS is timestamped
    # against .razor.css sources. Clean the matching release/RID build so renamed
    # assemblies cannot ship with stale component styles.
    & dotnet restore (Join-Path $repo 'CloneDeploy-Server/CloneDeploy-Server.csproj') -r win-x64 -p:SelfContained=false
    if ($LASTEXITCODE) { throw 'Server restore failed.' }
    & dotnet clean (Join-Path $repo 'CloneDeploy-Server/CloneDeploy-Server.csproj') -c Release -r win-x64 -p:SelfContained=false
    if ($LASTEXITCODE) { throw 'Server clean failed.' }
    if (Test-Path -LiteralPath $payload) {
        $resolved = (Resolve-Path -LiteralPath $payload).Path
        if ($resolved -ne [IO.Path]::GetFullPath($payload) -or (Get-Item -LiteralPath $payload).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected staging directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    & dotnet publish (Join-Path $repo 'CloneDeploy-Server/CloneDeploy-Server.csproj') -c Release -r win-x64 --self-contained false "-p:Version=$Version" -p:DebugType=None -o $payload
    if ($LASTEXITCODE) { throw 'Server publish failed.' }
    & dotnet publish (Join-Path $repo 'CloneDeploy-SiteService/CloneDeploy-SiteService.csproj') -c Release -r win-x64 --self-contained false "-p:Version=$Version" -p:DebugType=None -o (Join-Path $payload 'SiteService')
    if ($LASTEXITCODE) { throw 'SiteService publish failed.' }
    & dotnet publish (Join-Path $repo 'CloneDeploy-WindowsWorker/CloneDeploy-WindowsWorker.csproj') -c Release -r win-x64 --self-contained false "-p:Version=$Version" -p:DebugType=None -o (Join-Path $payload 'WindowsWorker')
    if ($LASTEXITCODE) { throw 'Windows Worker publish failed.' }
    $builderOutput = Join-Path $payload 'PeBuilder'
    & dotnet publish (Join-Path $repo 'CloneDeploy-WinPeBuilder/CloneDeploy-WinPeBuilder.csproj') -c Release -r win-x64 --self-contained false "-p:Version=$Version" -p:DebugType=None -o $builderOutput
    if ($LASTEXITCODE) { throw 'WinPE builder publish failed.' }
}
# Older staging directories may still contain the redundant standalone payload.
$oldMacArchive = Join-Path $payload 'Packaging/Agent/CloneDeploy-Agent-macOS-arm64.tar.gz'
if (Test-Path -LiteralPath $oldMacArchive) { Remove-Item -LiteralPath $oldMacArchive -Force }
foreach ($required in @('SiteService/CloneDeploy-SiteService.exe','CloneDeploy-Server.exe','Packaging/Imager/win-x64.zip','Packaging/Imager/win-arm64.zip','Packaging/Agent/CloneDeploy-Agent-x64.msi','Packaging/Agent/CloneDeploy-Agent-x64.msi.json','Packaging/Agent/CloneDeploy-Agent-arm64.msi','Packaging/Agent/CloneDeploy-Agent-arm64.msi.json','Packaging/Agent/CloneDeploy-Agent-macOS-arm64.pkg','Packaging/Agent/CloneDeploy-Agent-macOS-arm64.pkg.json','PeBuilder/CloneDeploy-WinPeBuilder.exe','PeBuilder/CloneDeploy-WinPeBuilder.runtimeconfig.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing release payload: $required. Run Build-Release.ps1 first." }
}
if ($Role -eq 'WindowsWorker' -and !(Test-Path -LiteralPath (Join-Path $payload 'WindowsWorker/CloneDeploy-WindowsWorker.exe'))) { throw 'Publish the Windows Worker payload before building its installer.' }
if ($Role -eq 'SiteService' -and (Test-Path -LiteralPath (Join-Path $payload 'SiteService/CloneDeploy-WinPe.dll'))) { throw 'Stale combined Site Service payload detected. Republish before packaging.' }
if ($Role -eq 'WindowsWorker' -and (Test-Path -LiteralPath (Join-Path $payload 'WindowsWorker/Yarp.ReverseProxy.dll'))) { throw 'Windows Worker payload must not contain a reverse proxy.' }
& (Join-Path $PSScriptRoot 'Validate-FrameworkDeployment.ps1') -Path $payload

if ($Role -eq 'App') {
    & (Join-Path $PSScriptRoot 'Validate-PublishedStyles.ps1') -Path $payload
}

# Sign before building archives/MSIs: signatures change the bytes and their hashes.
$publisher = $null
if (!$SkipSigning) {
    $binaries = @(Get-ChildItem -LiteralPath $payload -Recurse -File |
        Where-Object { $_.Name -like 'CloneDeploy*' -and $_.Extension -in @('.exe', '.dll') } | ForEach-Object FullName)
    & (Join-Path $repo 'Build/Sign-WindowsArtifact.ps1') -Path $binaries @signing -VerifyOnly:$SkipPublish
    . (Join-Path $repo 'CloneDeploy-Agent.Win/Updates/Verify-AgentPublisher.ps1')
    $publisher = Get-AgentSigningProfile (Join-Path $payload 'CloneDeploy-Server.exe')
    & (Join-Path $repo 'Build/Sign-WindowsArtifact.ps1') -Path $customActionPath @signing -ExpectedProfile $publisher
    foreach ($architecture in @('x64', 'arm64')) {
        & (Join-Path $repo 'Build/Sign-ImagerBundle.ps1') -Path (Join-Path $payload "Packaging/Imager/win-$architecture.zip") -Signing $signing -VerifyOnly:$SkipPublish -ExpectedProfile $publisher
        # Already signed by the agent builder; never change an MSI after hashing it.
        & (Join-Path $repo 'Build/Sign-WindowsArtifact.ps1') -Path (Join-Path $payload "Packaging/Agent/CloneDeploy-Agent-$architecture.msi") @signing -VerifyOnly -ExpectedProfile $publisher
    }
}
if (!$SkipPublish) {
    Compress-Archive -Path (Join-Path $payload 'PeBuilder/*') -DestinationPath (Join-Path $repo 'artifacts/CloneDeploy-WinPeBuilder-win-x64.zip') -Force
}

# Publish only the baseline settings; never package developer credentials, logs or keys.
foreach ($name in @('appsettings.Development.json', 'web.config')) {
    $path = Join-Path $payload $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
$settings = Get-Content -LiteralPath (Join-Path $repo 'CloneDeploy-Server/appsettings.json') -Raw | ConvertFrom-Json
$settings.PSObject.Properties.Remove('ConnectionStrings')
[IO.File]::WriteAllText((Join-Path $payload 'appsettings.json'), ($settings | ConvertTo-Json -Depth 12))
foreach ($folder in @('keys','logs','Tests')) {
    $path = Join-Path $payload $folder
    if (Test-Path -LiteralPath $path) { throw "Unexpected private or development directory in publish output: $folder" }
}
# Tools contains the explicitly bundled native dependencies (including UDPcast).
# Publish excludes the source development Tools directory in the project file.
$setup = Join-Path $payload 'Setup'
[void][IO.Directory]::CreateDirectory($setup)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Configure-Server.ps1'), (Join-Path $PSScriptRoot 'ServerFirewall.ps1'), (Join-Path $PSScriptRoot 'Configure-SiteService.ps1'), (Join-Path $PSScriptRoot 'AccountRights.cs') -Destination $setup -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SiteServiceFirewall.ps1'), (Join-Path $PSScriptRoot 'MulticastFirewall.ps1') -Destination $setup -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ServerSecrets.ps1') -Destination $setup -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'server.template.json') -Destination $setup -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CustomActions/LICENSE-WIX.txt') -Destination $setup -Force

# Include the complete PostgreSQL runtime, including extensions and license files.
# pgAdmin, headers, documentation and Stack Builder are not runtime dependencies.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pgPayload = Join-Path $payload 'PostgreSQL/18'
[void][IO.Directory]::CreateDirectory($pgPayload)
$zip = [IO.Compression.ZipFile]::OpenRead($PostgreSqlArchive)
try {
    $postgres = @($zip.Entries | Where-Object { $_.FullName -match '(^|/)bin/postgres\.exe$' })
    if ($postgres.Count -ne 1) { throw 'Expected one PostgreSQL Windows runtime in the archive.' }
    $prefix = $postgres[0].FullName.Substring(0, $postgres[0].FullName.Length - 'bin/postgres.exe'.Length)
    foreach ($entry in $zip.Entries) {
        if (!$entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { continue }
        $relative = $entry.FullName.Substring($prefix.Length)
        if (!$entry.Name -or $relative -notmatch '^(bin/|lib/|share/|[^/]*(LICENSE|COPYRIGHT|NOTICE)[^/]*$)') { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $pgPayload $relative))
        if (!$target.StartsWith([IO.Path]::GetFullPath($pgPayload) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive entry.' }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $zip.Dispose() }
foreach ($required in @('bin/postgres.exe','bin/pg_ctl.exe','bin/initdb.exe','bin/psql.exe','bin/pg_dump.exe','bin/pg_restore.exe','share/extension/citext.control')) {
    if (!(Test-Path -LiteralPath (Join-Path $pgPayload $required))) { throw "PostgreSQL archive is incomplete: $required" }
}
$pgVersion = & (Join-Path $pgPayload 'bin/postgres.exe') --version
if ($LASTEXITCODE -or $pgVersion -notmatch '\b18\.\d+') { throw 'A working PostgreSQL 18 x64 distribution is required.' }

[xml]$aip = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CloneDeploy-Server.aip')
function Get-Part([string]$Name) {
    $part = $aip.SelectSingleNode('//COMPONENT[@cid="caphyon.advinst.msicomp.' + $Name + '"]')
    if (!$part) { $part = $aip.CreateElement('COMPONENT'); $part.SetAttribute('cid', 'caphyon.advinst.msicomp.' + $Name); [void]$aip.DOCUMENT.AppendChild($part) }
    return ,$part
}
function Add-Row([string]$Name, [hashtable]$Values) {
    $row = $aip.CreateElement('ROW')
    foreach ($key in $Values.Keys) { $row.SetAttribute($key, [string]$Values[$key]) }
    [void](Get-Part $Name).AppendChild($row)
}
function Get-StableGuid([string]$Value) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes('CloneDeploy-Server.Server.Installer/' + $Value.ToLowerInvariant())) }
    finally { $hash.Dispose() }
    return (New-Object Guid (,[byte[]]$bytes[0..15])).ToString('B').ToUpperInvariant()
}
$aip.SelectSingleNode('//ROW[@Property="ProductVersion"]').Value = $Version
$aip.SelectSingleNode('//ROW[@Property="ProductCode"]').Value = '1033:' + (Get-StableGuid "product/$Role/$Version") + ' '
. (Join-Path $PSScriptRoot 'Set-InstallerRole.ps1')
Set-InstallerRole -Document $aip -Role $Role
$aip.SelectSingleNode('//ROW[@BuildKey="DefaultBuild"]').SetAttribute('PackageFolder', $packageStage)
$aip.SelectSingleNode('//ROW[@Name="CloneDeploy.ico"]').SetAttribute('SourcePath', (Join-Path $repo 'Branding/CloneDeploy.ico'))
$aip.SelectSingleNode('//ROW[@Name="ServerCustomActions"]').SetAttribute('SourcePath', (Join-Path $PSScriptRoot 'CustomActions/bin/Release/net462/CloneDeploy.Server.Setup.CA.dll'))
$directories = @{ '' = 'APPDIR' }
$components = @('InstallationInformation', $(if ($Role -eq 'App') { 'ServerInformation' } else { "${Role}Information" }))
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName)
foreach ($file in $files) {
    $relative = $file.FullName.Substring($payload.Length + 1)
    $shared = $relative -in @('Setup\LICENSE-WIX.txt', 'Setup\AccountRights.cs')
    $siteFile = $relative.StartsWith('SiteService\') -or $relative -in @('Setup\Configure-SiteService.ps1', 'Setup\SiteServiceFirewall.ps1', 'Setup\MulticastFirewall.ps1')
    $workerFile = $relative.StartsWith('WindowsWorker\') -or $relative -eq 'Setup\Configure-SiteService.ps1'
    if (!$shared -and (($Role -eq 'WindowsWorker' -and !$workerFile) -or ($Role -eq 'SiteService' -and !$siteFile) -or
        ($Role -eq 'App' -and ($relative.StartsWith('SiteService\') -or $relative.StartsWith('WindowsWorker\') -or $relative -in @('Setup\Configure-SiteService.ps1','Setup\SiteServiceFirewall.ps1'))))) { continue }
    $folder = [IO.Path]::GetDirectoryName($relative)
    $parts = if ($folder) { $folder.Split('\') } else { @() }
    $parent = 'APPDIR'; $current = ''
    foreach ($name in $parts) {
        $current = if ($current) { "$current\$name" } else { $name }
        if (!$directories.ContainsKey($current)) {
            $id = 'd' + (Get-StableGuid $current).Trim('{}').Replace('-', '')
            Add-Row MsiDirsComponent @{ Directory = $id; Directory_Parent = $parent; DefaultDir = ('D' + $directories.Count.ToString('0000000') + '|' + $name) }
            $directories[$current] = $id
        }
        $parent = $directories[$current]
    }
    $id = 'f' + (Get-StableGuid $relative).Trim('{}').Replace('-', '')
    $roleCondition = 'INSTALL_ROLE="' + $Role + '"'
    Add-Row MsiCompsComponent @{ Condition = $roleCondition; Component = $id; ComponentId = (Get-StableGuid "component/$Role/$relative"); Directory_ = $parent; Attributes = '256'; KeyPath = $id }
    Add-Row MsiFilesComponent @{ File = $id; Component_ = $id; FileName = ('F' + $components.Count.ToString('0000000') + '|' + $file.Name); Attributes = '0'; SourcePath = $file.FullName; SelfReg = 'false' }
    $components += $id
}
$aip.SelectSingleNode('//ROW[@Feature="MainFeature"]').SetAttribute('Components', ($components -join ' '))
$project = Join-Path $output ($packageName + '.aip')
$aip.Save($project)
& $AdvancedInstaller /rebuild $project
if ($LASTEXITCODE) { throw 'Advanced Installer build failed.' }

$msi = Join-Path $packageStage ($packageName + '-x64.msi')
& (Join-Path $repo 'Build/Set-MsiSummary.ps1') -Path $msi
if (!$SkipSigning) {
    & (Join-Path $repo 'Build/Sign-WindowsArtifact.ps1') -Path $msi @signing -ExpectedProfile $publisher
}
& (Join-Path $PSScriptRoot 'Validate-ServerInstaller.ps1') -Path $msi -Role $Role
$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
$destination = Join-Path $output ($packageName + '-x64.msi')
$temporary = $destination + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::Copy($msi, $temporary)
    if ([IO.File]::Exists($destination)) { [IO.File]::Replace($temporary, $destination, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $destination) }
} finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
$msi = $destination
[IO.File]::WriteAllText((Join-Path $output ($packageName + '-x64.msi.sha256')), "$hash  $packageName-x64.msi`r`n")
[xml]$runtimeProperties = Get-Content -LiteralPath (Join-Path $repo 'Build/ServerRuntime.props') -Raw
$release = @{ Version = $Version; Role = $Role; InstallerSha256 = $hash; Deployment = 'FrameworkDependent'; RequiredDotNet = [string]$runtimeProperties.Project.PropertyGroup.RuntimeFrameworkVersion; RequiredVisualCpp = '14.44.35211.0' }
$release.Signed = !$SkipSigning
if ($Role -eq 'App') {
    $release.PostgreSQL = $pgVersion
    $release.PostgreSqlArchiveSha256 = (Get-FileHash -LiteralPath $PostgreSqlArchive -Algorithm SHA256).Hash
}
$release |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release.json')
Write-Host "Windows installer ready: $msi (signed: $(!$SkipSigning))"
