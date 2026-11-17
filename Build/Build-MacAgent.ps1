[CmdletBinding()]
param([ValidateRange(0,65534)][int]$Version = 0, [switch]$NoUpdateReleaseState)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($NoUpdateReleaseState -and $Version -eq 0) { throw 'Managed release builds must supply the planned Mac version.' }
$project = Join-Path $repoRoot 'CloneDeploy-Agent.MacOS/CloneDeploy-Agent.MacOS.csproj'
$versionFile = Join-Path $repoRoot 'CloneDeploy-Agent.MacOS/AgentVersion.props'
$output = Join-Path $repoRoot 'artifacts/mac-agent/osx-arm64'
[xml]$projectXml = Get-Content -LiteralPath $versionFile -Raw
$sourceVersion = ([version][string]$projectXml.Project.PropertyGroup.Version).Build
$lastRelease = Join-Path $repoRoot 'artifacts/mac-agent/release.json'
$previousVersion = 0
if (Test-Path -LiteralPath $lastRelease) { $previousVersion = ([version](Get-Content -LiteralPath $lastRelease -Raw | ConvertFrom-Json).Version).Build }
elseif (Test-Path -LiteralPath (Join-Path $output 'release.json')) { $previousVersion = ([version](Get-Content -LiteralPath (Join-Path $output 'release.json') -Raw | ConvertFrom-Json).Version).Build }
if ($Version -eq 0) { $Version = [Math]::Max($sourceVersion, $previousVersion + 1) }
if ($Version -lt $sourceVersion -or $Version -lt $previousVersion -or $Version -gt 65534) { throw 'Mac version must not go backwards and must be at most 65534.' }
$releaseVersion = "2.0.$Version.0"
& dotnet publish $project -c Release -r osx-arm64 --self-contained true -p:DebugType=None "-p:Version=$releaseVersion" -o $output
if ($LASTEXITCODE -ne 0) { throw 'Mac agent publish failed.' }
# The portal is a separate user process; it never receives the daemon's private state.
# Re-publishing does not remove obsolete DLLs. Recreate only this known bundle.
$portalBundle = [IO.Path]::GetFullPath((Join-Path $output 'CloneDeploy Portal.app'))
if (Test-Path -LiteralPath $portalBundle) {
    if ((Get-Item -LiteralPath $portalBundle).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
        [IO.Path]::GetDirectoryName($portalBundle) -ne [IO.Path]::GetFullPath($output)) { throw 'Unexpected portal bundle path.' }
    Remove-Item -LiteralPath $portalBundle -Recurse -Force
}
$bundle = Join-Path $portalBundle 'Contents'
$portalOutput = Join-Path $bundle 'MacOS'
& dotnet publish (Join-Path $repoRoot 'CloneDeploy-Agent.MacOS/Portal/CloneDeploy-Agent.MacOS.Portal.csproj') -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=None "-p:Version=$releaseVersion" -o $portalOutput
if ($LASTEXITCODE -ne 0) { throw 'Mac portal publish failed.' }
if (Get-ChildItem -LiteralPath $portalOutput -Recurse -Filter '*.dll') {
    throw 'The portal bundle must not contain loose managed DLLs in Contents/MacOS.'
}
$info = [IO.File]::ReadAllText((Join-Path $repoRoot 'CloneDeploy-Agent.MacOS/Packaging/portal-Info.plist')).Replace('__VERSION__', "2.0.$Version")
[IO.File]::WriteAllText((Join-Path $bundle 'Info.plist'), $info, [Text.UTF8Encoding]::new($false))
$resources = Join-Path $bundle 'Resources'
New-Item -ItemType Directory -Path $resources -Force | Out-Null
# License text is bundle data, not nested executable code.
Get-ChildItem -LiteralPath $portalOutput -Filter '*.txt' -File | ForEach-Object {
    Move-Item -LiteralPath $_.FullName -Destination $resources
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'Branding/CloneDeploy.icns') -Destination $resources
$portalInstaller = [IO.File]::ReadAllText((Join-Path $repoRoot 'CloneDeploy-Agent.MacOS/Packaging/install-portal.sh')).Replace("`r`n", "`n")
[IO.File]::WriteAllText((Join-Path $output 'install-portal.sh'), $portalInstaller, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repoRoot 'CloneDeploy-Agent.MacOS/Packaging/com.clonedeploy.agent.portal.plist') -Destination $output
# Preserve Unix line endings even when the checkout uses Windows CRLF.
$uninstaller = [IO.File]::ReadAllText((Join-Path $repoRoot 'CloneDeploy-Agent.MacOS/Packaging/uninstall.sh')).Replace("`r`n", "`n")
[IO.File]::WriteAllText((Join-Path $output 'uninstall.sh'), $uninstaller, [Text.UTF8Encoding]::new($false))
$installer = [IO.File]::ReadAllText((Join-Path $repoRoot 'CloneDeploy-Agent.MacOS/Packaging/install.sh')).Replace("`r`n", "`n")
[IO.File]::WriteAllText((Join-Path $output 'install.sh'), $installer, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repoRoot 'CloneDeploy-Agent.MacOS/Packaging/com.clonedeploy.agent.plist') -Destination $output
[IO.File]::WriteAllText((Join-Path $output 'release.json'), (@{ Version = $releaseVersion } | ConvertTo-Json))
$archive = Join-Path $repoRoot 'artifacts/mac-agent/CloneDeploy-Agent-macOS-arm64.tar.gz'
& tar -czf $archive -C $output .
if ($LASTEXITCODE -ne 0) { throw 'Mac agent archive creation failed.' }
$package = Join-Path $repoRoot 'artifacts/mac-agent/CloneDeploy-Agent-macOS-arm64.pkg'
& dotnet run --project (Join-Path $repoRoot 'Build/MacPackage/MacPackage.csproj') -c Release -- $archive $package $releaseVersion
if ($LASTEXITCODE -ne 0) { throw 'Mac agent package creation failed.' }
& (Join-Path $repoRoot 'Build/Write-MacPackageManifest.ps1') -ArtifactDirectory (Split-Path $package) -Version $releaseVersion
if (!$NoUpdateReleaseState) { [IO.File]::WriteAllText($lastRelease, (@{ Version = $releaseVersion } | ConvertTo-Json)) }
Write-Host "Mac agent version ${Version}: $package"
Write-Host "Preparation/update archive: $archive"
Write-Host 'The build-time .pkg updates an already configured Mac. For new enrollment, sign/notarize the release and use Prepare agent release in the web UI to bundle separate configuration.'
Write-Host 'This development package is unsigned and not notarized.'
