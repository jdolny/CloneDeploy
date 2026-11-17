[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.@-]*$')][string]$MacHost,
    [Parameter(Mandatory)][string]$ApplicationIdentity,
    [Parameter(Mandatory)][string]$InstallerIdentity,
    [Parameter(Mandatory)][string]$NotaryProfile,
    [Parameter(Mandatory)][string]$ArtifactDirectory
)
$ErrorActionPreference = 'Stop'
function Quote-Shell([string]$Value) { return "'" + $Value.Replace("'", "'" + '"' + "'" + '"' + "'") + "'" }
$remote = (& ssh -o BatchMode=yes -- $MacHost 'mktemp -d /private/tmp/clonedeploy-sign.XXXXXXXX').Trim()
if ($LASTEXITCODE -ne 0 -or $remote -notmatch '^/private/tmp/clonedeploy-sign\.[A-Za-z0-9]+$') { throw 'Could not create signing workspace on the Mac.' }
$package = Join-Path $ArtifactDirectory 'CloneDeploy-Agent-macOS-arm64.pkg'
$archive = Join-Path $ArtifactDirectory 'CloneDeploy-Agent-macOS-arm64.tar.gz'
try {
    & scp -B -- $package "${MacHost}:$remote/unsigned.pkg"
    if ($LASTEXITCODE) { throw 'Could not upload Mac installer.' }
    # Keep shell input LF-only regardless of checkout settings.
    $script = Join-Path $ArtifactDirectory 'sign-mac-release.sh'
    [IO.File]::WriteAllText($script, [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Sign-MacRelease.sh')).Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    & scp -B -- $script "${MacHost}:$remote/sign.sh"
    if ($LASTEXITCODE) { throw 'Could not upload Mac signing script.' }
    $command = '/bin/bash ' + (Quote-Shell "$remote/sign.sh") + ' ' + ((@($remote, $ApplicationIdentity, $InstallerIdentity, $NotaryProfile) | ForEach-Object { Quote-Shell $_ }) -join ' ')
    & ssh -o BatchMode=yes -- $MacHost $command
    if ($LASTEXITCODE) { throw "Mac signing/notarization failed. Inspect $remote on the signing Mac." }
    & scp -B -- "${MacHost}:$remote/signed.pkg" "$package.signed"
    if ($LASTEXITCODE) { throw 'Could not download signed package.' }
    & scp -B -- "${MacHost}:$remote/agent.tar.gz" "$archive.signed"
    if ($LASTEXITCODE) { throw 'Could not download signed payload archive.' }
    Move-Item -LiteralPath "$package.signed" -Destination $package -Force
    Move-Item -LiteralPath "$archive.signed" -Destination $archive -Force
    $manifestVersion = (Get-Content -LiteralPath "$package.json" -Raw | ConvertFrom-Json).Version
    & (Join-Path $PSScriptRoot 'Write-MacPackageManifest.ps1') -ArtifactDirectory $ArtifactDirectory -Version $manifestVersion
    # The remote directory is retained for notarization diagnostics; it contains no enrollment data.
    Write-Host "Mac signing verified. Remote diagnostics: $remote"
} finally {
    if ($script -and (Test-Path -LiteralPath $script)) { Remove-Item -LiteralPath $script }
}
