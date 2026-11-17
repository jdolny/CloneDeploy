[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
[xml]$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../Build/ServerRuntime.props') -Raw
$minimum = [string]$props.Project.PropertyGroup.RuntimeFrameworkVersion
foreach ($relative in @('CloneDeploy-Server', 'SiteService/CloneDeploy-SiteService', 'WindowsWorker/CloneDeploy-WindowsWorker', 'PeBuilder/CloneDeploy-WinPeBuilder')) {
    $base = Join-Path $Path $relative
    $config = Get-Content -LiteralPath ($base + '.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtime = $config.runtimeOptions
    if ($runtime.includedFrameworks -or $runtime.rollForward -ne 'LatestPatch') { throw "Expected framework-dependent patch roll-forward: $relative" }
    $frameworks = @($runtime.frameworks) + @($runtime.framework) | Where-Object { $_ }
    if (!($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })) { throw "Missing shared .NET reference: $relative" }
    if (!($frameworks | Where-Object { $_.name -eq 'Microsoft.AspNetCore.App' })) { throw "Missing shared ASP.NET Core reference: $relative" }
    foreach ($framework in $frameworks) {
        if ($framework.version -ne $minimum) { throw "Unexpected runtime requirement for $relative : $($framework.version)" }
    }
    foreach ($file in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll')) {
        if (Test-Path -LiteralPath (Join-Path (Split-Path $base) $file)) { throw "Bundled runtime remains in $relative : $file. Publish to an empty directory." }
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($architecture in @('x64', 'arm64')) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Path "Packaging/Imager/win-$architecture.zip"))
    try {
        if (!$archive.GetEntry('coreclr.dll')) { throw "Imager $architecture must remain self-contained." }
    } finally { $archive.Dispose() }
}
Write-Host 'Server roles use shared .NET; both Imager packages remain self-contained.'
