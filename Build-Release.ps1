[CmdletBinding()]
param(
    [string]$Version,
    [string]$AgentVersion,
    [string]$MacAgentVersion,
    [string]$PostgreSqlArchive,
    [string]$AdvancedInstaller = (Join-Path ${env:ProgramFiles(x86)} 'Caphyon\Advanced Installer 15.1\bin\x86\AdvancedInstaller.com'),
    [switch]$SkipSigning,
    [string]$SignTool = 'C:\Microsoft Trusted Signing\Microsoft.Windows.SDK.BuildTools\bin\10.0.22621.0\x64\signtool.exe',
    [string]$SigningDll = 'C:\Microsoft Trusted Signing\Microsoft.Trusted.Signing.Client\bin\x64\Azure.CodeSigning.Dlib.dll',
    [string]$SigningProfile = 'C:\Microsoft Trusted Signing\profile.json',
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build/ReleaseVersioning.ps1')
$statePath = Join-Path $PSScriptRoot 'Release-VersionState.json'
$versionFile = Join-Path $PSScriptRoot 'CloneDeploy-Agent.Win/AgentVersion.props'
$macVersionFile = Join-Path $PSScriptRoot 'CloneDeploy-Agent.MacOS/AgentVersion.props'
$buildLock = $null
try {
    if (!$Preview) {
        # Keep two full release builds from selecting the same version or sharing output.
        $buildLock = [IO.File]::Open((Join-Path $PSScriptRoot '.release-build.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    }
    if (Test-Path -LiteralPath $statePath) {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if (!$state.ServerVersion -or $null -eq $state.AgentVersion -or $null -eq $state.AgentFingerprint) {
            throw 'Release-VersionState.json is incomplete. Restore the last successful release state.'
        }
    } else {
        $lastServer = '2.0.0'
        $manifest = Join-Path $PSScriptRoot 'artifacts/server-installer/release.json'
        if (Test-Path -LiteralPath $manifest) { $lastServer = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).Version }
        $state = [pscustomobject]@{ ServerVersion = $lastServer; AgentVersion = 0; AgentFingerprint = ''; MacAgentVersion = 0; MacAgentFingerprint = '' }
    }
    # Standalone builds advance this manifest independently of the full-release baseline.
    # Prefer the committed local release marker over publish output: managed builds may
    # have overwritten the latter before a later installer failed.
    $lastBuiltMac = 0
    $macManifest = Join-Path $PSScriptRoot 'artifacts/mac-agent/release.json'
    if (!(Test-Path -LiteralPath $macManifest)) { $macManifest = Join-Path $PSScriptRoot 'artifacts/mac-agent/osx-arm64/release.json' }
    if (Test-Path -LiteralPath $macManifest) { $lastBuiltMac = ([version](Get-Content -LiteralPath $macManifest -Raw | ConvertFrom-Json).Version).Build }
    if ($null -eq $state.MacAgentVersion -or $null -eq $state.MacAgentFingerprint) {
        throw 'Mac release state is incomplete. Restore the last successful release state.'
    }
    [xml]$macXml = Get-Content -LiteralPath $macVersionFile -Raw
    $sourceMac = ([version][string]$macXml.Project.PropertyGroup.Version).Build
    [xml]$agentXml = Get-Content -LiteralPath $versionFile -Raw
    $sourceAgent = [int]$agentXml.Project.PropertyGroup.AgentReleaseNumber
    $sdk = & dotnet --version
    if ($LASTEXITCODE) { throw 'Could not determine the .NET SDK version.' }
    $fingerprint = Get-AgentInputFingerprint $PSScriptRoot "$sdk"
    $plan = Get-ReleasePlan $state $sourceAgent $fingerprint $Version $AgentVersion
    $macFingerprint = Get-AgentInputFingerprint $PSScriptRoot "$sdk" -Platform Mac
    $macPlan = Get-MacReleasePlan $state $sourceMac $macFingerprint $MacAgentVersion -LastBuiltMac $lastBuiltMac
    Write-Host "Mac agent version: $($macPlan.MacAgentVersion); Mac inputs changed: $($macPlan.MacAgentChanged)"
    if (!$state.MacAgentFingerprint) { Write-Host 'No previous Mac fingerprint: selecting a new Mac release to establish the baseline.' }
    Write-Host "Server version: $($plan.ServerVersion); agent version: $($plan.AgentVersion); agent inputs changed: $($plan.AgentChanged)"
    if (!$state.AgentFingerprint) { Write-Host 'No previous agent fingerprint: selecting a new release to establish the baseline.' }
    if ($Preview) { return }
    $signing = @{ SignTool = $SignTool; SigningDll = $SigningDll; SigningProfile = $SigningProfile }
    if (!$SkipSigning) {
        $macGitHub = & (Join-Path $PSScriptRoot 'Build/Get-GitHubMacRelease.ps1') -CheckOnly
        foreach ($required in $signing.Values) {
            if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Signing input is missing: $required" }
        }
    }

    # Save the initial baseline before any installer overwrites its old release manifest.
    # Failed builds then retry the same versions, even if the server installer completed.
    if (!(Test-Path -LiteralPath $statePath)) { Save-ReleaseState $statePath $state }
    $agent = @{ Version = [string]$plan.AgentVersion; NoUpdateVersionFile = $true; SkipSigning = $SkipSigning; SignTool = $SignTool; SigningDll = $SigningDll; SigningProfile = $SigningProfile }
    & (Join-Path $PSScriptRoot 'CloneDeploy-Agent.Win/Packaging/Build-AgentInstaller.ps1') @agent
    & (Join-Path $PSScriptRoot 'CloneDeploy-Agent.Win/Packaging/Build-AgentInstaller.ps1') @agent -Architecture arm64

    if ($SkipSigning) {
        & (Join-Path $PSScriptRoot 'Build/Build-MacAgent.ps1') -Version $macPlan.MacAgentVersion -NoUpdateReleaseState
    } else {
        & (Join-Path $PSScriptRoot 'Build/Get-GitHubMacRelease.ps1') -Context $macGitHub -Version $macPlan.MacAgentVersion -Sdk "$sdk"
    }

    # The agent includes its UI and Windows remote desktop implementation. Server publish builds
    # the Imager; the server installer also publishes PeBuilder and SiteService.
    $server = @{ Version = $plan.ServerVersion; PostgreSqlArchive = $PostgreSqlArchive; AdvancedInstaller = $AdvancedInstaller; SkipSigning = $SkipSigning; SignTool = $SignTool; SigningDll = $SigningDll; SigningProfile = $SigningProfile }
    & (Join-Path $PSScriptRoot 'CloneDeploy-Server/Packaging/Build-ServerInstaller.ps1') @server
    & (Join-Path $PSScriptRoot 'CloneDeploy-Server/Packaging/Build-ServerInstaller.ps1') @server -Role SiteService -SkipPublish
    if ($LASTEXITCODE) { throw 'Site Service installer build failed.' }
    & (Join-Path $PSScriptRoot 'CloneDeploy-Server/Packaging/Build-ServerInstaller.ps1') @server -Role WindowsWorker -SkipPublish
    if ($LASTEXITCODE) { throw 'Windows Worker installer build failed.' }
    if (!$SkipSigning) {
        $installers = @(
            'artifacts/agent-installer/CloneDeploy-Agent-x64.msi',
            'artifacts/agent-installer/CloneDeploy-Agent-arm64.msi',
            'artifacts/server-installer/CloneDeploy-Server-x64.msi',
            'artifacts/siteservice-installer/CloneDeploy-SiteService-x64.msi',
            'artifacts/windowsworker-installer/CloneDeploy-WindowsWorker-x64.msi'
        ) | ForEach-Object { Join-Path $PSScriptRoot $_ }
        & (Join-Path $PSScriptRoot 'Build/Sign-WindowsArtifact.ps1') -Path $installers @signing -VerifyOnly
    }
    & (Join-Path $PSScriptRoot 'Build/Build-LinuxPackages.ps1') -Version $plan.ServerVersion
    if ((Get-AgentInputFingerprint $PSScriptRoot "$sdk") -ne $fingerprint) {
        throw 'Agent inputs changed during the build. Rebuild before distributing this release; versions have not advanced.'
    }
    if ((Get-AgentInputFingerprint $PSScriptRoot "$sdk" -Platform Mac) -ne $macFingerprint) {
        throw 'Mac agent inputs changed during the build. Rebuild before distributing this release; versions have not advanced.'
    }
    # All builds and checks use generic names. Rename distributables only after they pass.
    & (Join-Path $PSScriptRoot 'Build/Rename-ReleasePackages.ps1') -Version $plan.ServerVersion
    Save-ReleaseState $statePath ([ordered]@{ ServerVersion = $plan.ServerVersion; AgentVersion = $plan.AgentVersion; AgentFingerprint = $fingerprint; MacAgentVersion = $macPlan.MacAgentVersion; MacAgentFingerprint = $macFingerprint })
    $content = [IO.File]::ReadAllText($versionFile) -replace '<AgentReleaseNumber>\d+</AgentReleaseNumber>', "<AgentReleaseNumber>$($plan.AgentVersion)</AgentReleaseNumber>"
    [IO.File]::WriteAllText($versionFile, $content)
    $productVersionFile = Join-Path $PSScriptRoot 'Directory.Build.props'
    if (Test-Path -LiteralPath $productVersionFile) {
        $productContent = [IO.File]::ReadAllText($productVersionFile) -replace '<Version>[^<]+</Version>', "<Version>$($plan.ServerVersion)</Version>"
        [IO.File]::WriteAllText($productVersionFile, $productContent)
    }
    $macContent = [IO.File]::ReadAllText($macVersionFile) -replace '<Version>[^<]+</Version>', "<Version>2.0.$($macPlan.MacAgentVersion).0</Version>"
    [IO.File]::WriteAllText($macVersionFile, $macContent)
    Save-ReleaseState (Join-Path $PSScriptRoot 'artifacts/mac-agent/release.json') ([ordered]@{ Version = "2.0.$($macPlan.MacAgentVersion).0" })
    Write-Host 'Release complete. Commit Release-VersionState.json, Directory.Build.props and both agent version files with the release.'
    if ($SkipSigning) { Write-Host 'Signing skipped. Packages are for development testing.' }
    else { Write-Host 'Windows products are signed and verified. The Mac package is signed, notarized and stapled.' }
} finally {
    if ($buildLock) { $buildLock.Dispose() }
}
