[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'

# Check compiled Razor string literals without loading/running the server assembly.
# Two alignments cover UTF-16 strings at odd/even offsets in the metadata heap.
$css = [IO.File]::ReadAllText((Join-Path $Path 'wwwroot/CloneDeploy-Server.styles.css'))
$bytes = [IO.File]::ReadAllBytes((Join-Path $Path 'CloneDeploy-Server.dll'))
$even = [Text.Encoding]::Unicode.GetString($bytes)
$odd = [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
$scopes = @([regex]::Matches($css, '\[(b-[a-z0-9]{10})\]') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if (!$scopes.Count -or $css -notmatch '\.auth-card\[b-') { throw 'Published component stylesheet is missing the login layout.' }
foreach ($scope in $scopes) {
    if (!$even.Contains($scope) -and !$odd.Contains($scope)) {
        throw "Published CSS scope $scope does not match the application assembly. Clean and republish before packaging."
    }
}
Write-Host "Published component styles match the application ($($scopes.Count) CSS scopes)."
