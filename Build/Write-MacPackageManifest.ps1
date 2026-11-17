[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactDirectory, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$package = Join-Path $ArtifactDirectory 'CloneDeploy-Agent-macOS-arm64.pkg'
$manifest = [ordered]@{ Version = $Version; Sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash; Size = (Get-Item -LiteralPath $package).Length }
[IO.File]::WriteAllText("$package.json", ($manifest | ConvertTo-Json))
