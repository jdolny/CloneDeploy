# Maintainer import helper; normal builds use the files already in source control.
param([Parameter(Mandatory = $true)][string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../third_party/WinFsp/2.1.25156'))
if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory 'License.txt') -PathType Leaf)) { throw 'Include the upstream WinFsp License.txt with the binaries.' }
foreach ($name in @('winfsp-x64.dll', 'winfsp-x64.sys', 'winfsp-a64.dll', 'winfsp-a64.sys')) {
    $file = Join-Path $SourceDirectory $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing WinFsp file: $file" }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
    if ($version.FileMajorPart -ne 2 -or $version.FileMinorPart -ne 1 -or $version.FileBuildPart -ne 25156) {
        throw "Expected WinFsp 2.1.25156 to match the managed package: $file"
    }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($name in @('winfsp-x64.dll', 'winfsp-x64.sys', 'winfsp-a64.dll', 'winfsp-a64.sys', 'License.txt')) {
    Copy-Item -LiteralPath (Join-Path $SourceDirectory $name) -Destination (Join-Path $destination $name) -Force
}
Write-Output "Prepared WinFsp 2.1.25156 in $destination. No driver was installed on this computer."
