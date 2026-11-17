[CmdletBinding()]
param([switch]$Serve, [int]$Port = 8081)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & './Validate-Docs.ps1'
    & dotnet tool restore
    if ($LASTEXITCODE) { throw 'DocFX tool restore failed.' }
    & dotnet tool run docfx -- build docfx.json --warningsAsErrors
    if ($LASTEXITCODE) { throw 'Documentation build failed.' }
    & './Validate-Docs.ps1' -Built
    if ($Serve) {
        & dotnet tool run docfx -- serve _site --port $Port
        if ($LASTEXITCODE) { throw 'Documentation preview failed.' }
    }
} finally { Pop-Location }
