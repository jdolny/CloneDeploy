[CmdletBinding()]
param(
    [string]$ArchivePath,
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../../artifacts/prerequisites')
)
$ErrorActionPreference = 'Stop'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PostgreSqlArchive.json') -Raw | ConvertFrom-Json
if ($pin.FileName -notmatch '^postgresql-[0-9.]+-[0-9]+-windows-x64-binaries\.zip$' -or
    $pin.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$pin.SizeBytes -le 0 -or
    $pin.Url -ne ('https://get.enterprisedb.com/postgresql/' + $pin.FileName)) {
    throw 'Invalid PostgreSQL archive pin.'
}
function Assert-PinnedArchive([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "PostgreSQL archive not found: $Path" }
    if ((Get-Item -LiteralPath $Path).Length -ne [long]$pin.SizeBytes -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $pin.Sha256) {
        throw "PostgreSQL archive does not match pinned version $($pin.Version): $Path. Supply the exact pinned archive or remove an invalid cached copy and retry."
    }
}
if (![string]::IsNullOrWhiteSpace($ArchivePath)) {
    $resolved = [IO.Path]::GetFullPath($ArchivePath)
    Assert-PinnedArchive $resolved
    return $resolved
}
$cache = [IO.Path]::GetFullPath($CacheDirectory)
[void][IO.Directory]::CreateDirectory($cache)
$destination = Join-Path $cache $pin.FileName
if (Test-Path -LiteralPath $destination) {
    Assert-PinnedArchive $destination
    return $destination
}
# A unique partial file prevents interrupted downloads from becoming cache hits.
$temporary = Join-Path $cache ($pin.FileName + '.' + [guid]::NewGuid().ToString('N') + '.partial')
try {
    Write-Host "Downloading pinned PostgreSQL $($pin.Version) from EDB..."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $pin.Url -OutFile $temporary -UseBasicParsing -TimeoutSec 1800
    Assert-PinnedArchive $temporary
    try { [IO.File]::Move($temporary, $destination) }
    catch {
        # Another build may have finished downloading the same pin first.
        if (!(Test-Path -LiteralPath $destination -PathType Leaf)) { throw }
        Assert-PinnedArchive $destination
    }
    return $destination
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
