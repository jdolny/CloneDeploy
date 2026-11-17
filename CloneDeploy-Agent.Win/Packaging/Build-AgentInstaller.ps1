[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [switch]$NoUpdateVersionFile,
    [switch]$UnsignedDevelopment,
    [switch]$SkipSigning,
    [string]$SignTool = 'C:\Microsoft Trusted Signing\Microsoft.Windows.SDK.BuildTools\bin\10.0.22621.0\x64\signtool.exe',
    [string]$SigningDll = 'C:\Microsoft Trusted Signing\Microsoft.Trusted.Signing.Client\bin\x64\Azure.CodeSigning.Dlib.dll',
    [string]$SigningProfile = 'C:\Microsoft Trusted Signing\profile.json'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo $(if ($UnsignedDevelopment) { 'artifacts/agent-installer-development' } else { 'artifacts/agent-installer' })
$signArtifacts = !$UnsignedDevelopment -and !$SkipSigning
# A fresh staging directory avoids including stale files from an earlier publish.
$stage = Join-Path $output ($Architecture + '-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
$versionFile = Join-Path $repo 'CloneDeploy-Agent.Win/AgentVersion.props'
[xml]$agentXml = Get-Content -LiteralPath $versionFile
$sourceRelease = [string]$agentXml.Project.PropertyGroup.AgentReleaseNumber
$requestedRelease = if ($Version) { $Version } else { $sourceRelease }
$release = & (Join-Path $PSScriptRoot 'Get-AgentReleaseVersion.ps1') -Version $requestedRelease
$requestedVersion = $release.MsiVersion
$assemblyVersion = $release.AssemblyVersion
if ($release.Release -lt [int]$sourceRelease) { throw 'The agent release cannot be lower than the current source release.' }
if (!$NoUpdateVersionFile -and $Version -and $release.Release -ne [int]$sourceRelease) {
    $text = [IO.File]::ReadAllText($versionFile).Replace('<AgentReleaseNumber>' + $sourceRelease + '</AgentReleaseNumber>', '<AgentReleaseNumber>' + $release.Release + '</AgentReleaseNumber>')
    [IO.File]::WriteAllText($versionFile, $text)
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null
& dotnet publish (Join-Path $repo 'CloneDeploy-Agent.Win/CloneDeploy-Agent.Win.csproj') -c Release -r "win-$Architecture" --self-contained true "-p:Version=$assemblyVersion" -p:PublishSingleFile=false -p:DebugType=None -o $payload
if ($LASTEXITCODE) { throw 'Agent publish failed.' }
& dotnet publish (Join-Path $repo 'CloneDeploy-Agent.Win/Portal/CloneDeploy-Agent.Win.Portal.csproj') -c Release -r "win-$Architecture" --self-contained true "-p:Version=$assemblyVersion" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None -o (Join-Path $payload 'UI')
if ($LASTEXITCODE) { throw 'Agent UI publish failed.' }
foreach ($extra in @('CloneDeploy-Agent.runtimeconfig.json', 'ControlR-LICENSE.txt')) {
    $extraPath = Join-Path $payload "UI/$extra"
    if (Test-Path -LiteralPath $extraPath) { Remove-Item -LiteralPath $extraPath }
}
if (Get-ChildItem -LiteralPath $payload -Recurse -Filter 'appsettings*.json') { throw 'Development settings must never be shipped.' }
$machine = if ($Architecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
foreach ($native in @('CloneDeploy-Agent.exe', 'coreclr.dll', 'e_sqlite3.dll', 'UI/CloneDeploy-Agent.UI.exe')) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $payload $native))
    if ([BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 60) + 4) -ne $machine) {
        throw "The $Architecture payload contains a mismatched native binary: $native"
    }
}
function Escape-Xml([string]$value) { [Security.SecurityElement]::Escape($value) }
$signing = @{ SignTool = $SignTool; SigningDll = $SigningDll; SigningProfile = $SigningProfile }
if ($signArtifacts) {
    $binaries = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Name -like 'CloneDeploy*' -and $_.Extension -in @('.exe', '.dll') } | ForEach-Object FullName)
    & (Join-Path $repo 'Build/Sign-WindowsArtifact.ps1') -Path $binaries @signing
}
function Hash-Text([string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))).Replace('-', '') }
    finally { $sha.Dispose() }
}
$components = [Collections.Generic.List[string]]::new()
$directories = [Collections.Generic.List[string]]::new()
$directoryIds = @{ '.' = 'APPDIR' }
foreach ($dir in Get-ChildItem -LiteralPath $payload -Directory -Recurse | Sort-Object FullName) {
    $relative = $dir.FullName.Substring($payload.Length + 1)
    $id = 'D' + (Hash-Text $relative)
    $parent = [IO.Path]::GetDirectoryName($relative)
    if (!$parent) { $parent = '.' }
    $directoryIds[$relative] = $id
    $directories.Add('<DirectoryRef Id="' + $directoryIds[$parent] + '"><Directory Id="' + $id + '" Name="' + (Escape-Xml $dir.Name) + '" /></DirectoryRef>')
}
foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName) {
    $relative = $file.FullName.Substring($payload.Length + 1)
    $hash = Hash-Text $relative
    $fileId = if ($relative -eq 'CloneDeploy-Agent.exe') { 'AgentExe' } elseif ($relative -eq 'UI\CloneDeploy-Agent.UI.exe') { 'AgentPortalExe' } else { 'F' + $hash }
    $parent = [IO.Path]::GetDirectoryName($relative)
    if (!$parent) { $parent = '.' }
    $extra = ''
    $shortcut = ''
    if ($fileId -eq 'AgentExe') {
        $extra = @'
<ServiceInstall Id="AgentService" Name="CloneDeployAgent" DisplayName="CloneDeploy Agent" Type="ownProcess" Start="auto" ErrorControl="normal" Account="LocalSystem" Description="CloneDeploy device management agent" />
<ServiceControl Id="AgentServiceControl" Name="CloneDeployAgent" Start="install" Stop="both" Remove="uninstall" Wait="yes" />
'@
    } elseif ($fileId -eq 'AgentPortalExe') {
        $extra = '<RegistryValue Id="AgentPortalStartup" Root="HKLM" Key="SOFTWARE\Microsoft\Windows\CurrentVersion\Run" Name="CloneDeployPortal" Type="string" Value="&quot;[#AgentPortalExe]&quot; --tray" />'
    }
    if ($fileId -eq 'AgentPortalExe') {
        $shortcut = '<Shortcut Id="AgentPortalStartMenu" Directory="AgentProgramMenuFolder" Name="CloneDeploy Portal" Description="Open the CloneDeploy self-service portal" Advertise="yes" Icon="AgentPortal.exe" IconIndex="0" WorkingDirectory="' + $directoryIds[$parent] + '" />'
        $extra += '<RemoveFolder Id="RemoveAgentProgramMenuFolder" Directory="AgentProgramMenuFolder" On="uninstall" />'
    }
    # SQLite has a version but no language metadata; use the language-neutral MSI value.
    $language = if ($file.Name -eq 'e_sqlite3.dll') { ' DefaultLanguage="0"' } else { '' }
    $components.Add('<Component Id="C' + $hash + '" Directory="' + $directoryIds[$parent] + '" Guid="*" Bitness="always64"><File Id="' + $fileId + '" Source="' + (Escape-Xml $file.FullName) + '" KeyPath="yes"' + $language + '>' + $shortcut + '</File>' + $extra + '</Component>')
}
$generated = Join-Path $stage 'Payload.wxs'
[IO.File]::WriteAllText($generated, '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment>' + ($directories -join "`n") + '<ComponentGroup Id="AgentPayload">' + ($components -join "`n") + '</ComponentGroup></Fragment></Wix>')
$productCode = ([guid](Hash-Text "CloneDeploy.Agent.$Architecture/$requestedVersion").Substring(0, 32)).ToString('B')
& dotnet build (Join-Path $PSScriptRoot 'Installer/Agent.wixproj') -c Release "-p:InstallerPlatform=$Architecture" "-p:BaseIntermediateOutputPath=$stage/obj/" "-p:AgentVersion=$requestedVersion" "-p:AgentProductCode=$productCode" "-p:GeneratedPayloadPath=$generated" "-p:OutputPath=$stage/installer/"
if ($LASTEXITCODE) { throw 'Agent MSI build failed.' }
$msi = Join-Path $stage "installer/CloneDeploy-Agent-$Architecture.msi"
& (Join-Path $repo 'Build/Set-MsiSummary.ps1') -Path $msi
$profile = 'UNSIGNED-DEVELOPMENT'
if ($signArtifacts) {
    & (Join-Path $repo 'Build/Sign-WindowsArtifact.ps1') -Path $msi @signing
    . (Join-Path $PSScriptRoot '../Updates/Verify-AgentPublisher.ps1')
    $profile = Get-AgentSigningProfile (Join-Path $payload 'CloneDeploy-Agent.exe')
    foreach ($binary in $binaries) { Assert-AgentPublisher $binary $profile }
    Assert-AgentPublisher $msi $profile
}
& (Join-Path $PSScriptRoot 'Validate-AgentInstaller.ps1') -Path $msi -Architecture $Architecture
$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
[IO.File]::WriteAllText("$msi.sha256", "$hash  CloneDeploy-Agent-$Architecture.msi`r`n")
& (Join-Path $PSScriptRoot 'Write-AgentPackageManifest.ps1') -Path $msi -Architecture $Architecture -ExpectedVersion $requestedVersion
# Only publish after signing and all package checks succeed. Failed signing must
# never replace a previously distributable installer with an unsigned one.
foreach ($artifact in @($msi, "$msi.sha256", "$msi.json")) {
    $destination = Join-Path $output ([IO.Path]::GetFileName($artifact))
    $temporary = $destination + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::Copy($artifact, $temporary)
        if ([IO.File]::Exists($destination)) { [IO.File]::Replace($temporary, $destination, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $destination) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
Write-Host "Generic $Architecture installer ready: $output (signed: $signArtifacts)"
