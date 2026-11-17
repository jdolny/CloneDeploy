[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot '../artifacts')
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Supply a numeric release version.' }
$packages = @(
    'server-installer/CloneDeploy-Server-x64.msi',
    'siteservice-installer/CloneDeploy-SiteService-x64.msi',
    'windowsworker-installer/CloneDeploy-WindowsWorker-x64.msi',
    'linux-packages/CloneDeploy-Server-linux-x64.tar.gz',
    'linux-packages/CloneDeploy-SiteService-linux-x64.tar.gz'
)
# Check every input before changing names. Agent and Imager packages are not release-renamed.
$renames = foreach ($relative in $packages) {
    $source = Join-Path $ArtifactsDirectory $relative
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing release package: $source" }
    if (!(Test-Path -LiteralPath "$source.sha256" -PathType Leaf)) { throw "Missing release checksum: $source.sha256" }
    $name = [IO.Path]::GetFileName($source) -replace '-(linux-x64|x64)\.', "-$Version-`$1."
    [pscustomobject]@{
        Source = $source
        Destination = Join-Path ([IO.Path]::GetDirectoryName($source)) $name
        Name = $name
        Hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
foreach ($item in $renames) {
    Move-Item -LiteralPath $item.Source -Destination $item.Destination -Force
    [IO.File]::WriteAllText("$($item.Destination).sha256", "$($item.Hash)  $($item.Name)`n")
    Remove-Item -LiteralPath "$($item.Source).sha256"
    Write-Host "Release package: $($item.Destination)"
}
