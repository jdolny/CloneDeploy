[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][hashtable]$Signing,
    [switch]$VerifyOnly,
    [string]$ExpectedProfile
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = (Resolve-Path -LiteralPath $Path).Path
$stage = Join-Path (Split-Path $archive) ('signing-' + [guid]::NewGuid().ToString('N'))
$replacement = $archive + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $stage)
    foreach ($required in @('CloneDeploy-Imager.exe', 'CloneDeploy-Imager.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) { throw "Incomplete Imager archive: $required" }
    }
    $binaries = @(Get-ChildItem -LiteralPath $stage -Recurse -File |
        Where-Object { $_.Name -like 'CloneDeploy*' -and $_.Extension -in @('.exe', '.dll') } | ForEach-Object FullName)
    & (Join-Path $PSScriptRoot 'Sign-WindowsArtifact.ps1') -Path $binaries @Signing -VerifyOnly:$VerifyOnly -ExpectedProfile $ExpectedProfile
    if (!$VerifyOnly) {
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $replacement)
        [IO.File]::Replace($replacement, $archive, [NullString]::Value)
    }
} finally {
    if (Test-Path -LiteralPath $replacement) { Remove-Item -LiteralPath $replacement -Force }
    $parent = [IO.Path]::GetFullPath((Split-Path $archive)) + [IO.Path]::DirectorySeparatorChar
    if (![IO.Path]::GetFullPath($stage).StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid signing staging directory.' }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
