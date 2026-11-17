[CmdletBinding()]
param([switch]$CheckOnly, $Context, [int]$Version, [string]$Sdk)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Invoke-Gh([string[]]$Arguments) {
    $errorFile = [IO.Path]::GetTempFileName()
    try {
        # Windows PowerShell represents native stderr as error records. Capture it
        # separately so diagnostics cannot corrupt successful JSON output.
        $savedPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $result = & gh @Arguments 2> $errorFile
            $exitCode = $LASTEXITCODE
        } finally { $ErrorActionPreference = $savedPreference }
        $details = [IO.File]::ReadAllText($errorFile).Trim()
        if ($exitCode) { throw "GitHub CLI failed (exit $exitCode): gh $($Arguments -join ' ')`n$details" }
        if ($details) { Write-Warning $details }
        return $result
    } finally { Remove-Item -LiteralPath $errorFile -Force }
}
Push-Location $repoRoot
try {
    if ($CheckOnly) {
        if (!(Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Install GitHub CLI and run gh auth login before building a signed release.' }
        $dirty = & git status --porcelain --untracked-files=normal
        if ($LASTEXITCODE -or $dirty) { throw 'Commit and push all release source changes before building a signed release.' }
        $sha = & git rev-parse HEAD
        if ($LASTEXITCODE) { throw 'Cannot determine release commit.' }
        $branch = & git symbolic-ref --short HEAD
        if ($LASTEXITCODE) { throw 'Build a signed release from a pushed branch, not detached HEAD.' }
        $repository = Invoke-Gh @('repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner')
        $remoteSha = Invoke-Gh @('api', "repos/$repository/commits/$([Uri]::EscapeDataString($branch))", '--jq', '.sha')
        if ($remoteSha -ne $sha) { throw 'The GitHub branch must point to the current local commit. Push it before building.' }
        return [pscustomobject]@{ Repository = "$repository"; Branch = "$branch"; Sha = "$sha" }
    }
    if (!$Context -or $Version -lt 1 -or !$Sdk) { throw 'Missing GitHub release context, version, or SDK.' }
    $repository = $Context.Repository
    # Recheck after the Windows build, before dispatching a potentially moved branch.
    $remoteSha = Invoke-Gh @('api', "repos/$repository/commits/$([Uri]::EscapeDataString($Context.Branch))", '--jq', '.sha')
    if ($remoteSha -ne $Context.Sha) { throw 'GitHub branch moved during this build. Restart the release from the intended commit.' }
    $request = [guid]::NewGuid().ToString('N')
    $title = "Mac release $Version $request"
    Invoke-Gh @('workflow', 'run', 'mac-release.yml', '--repo', $repository, '--ref', $Context.Branch,
        '-f', "version=$Version", '-f', "request_id=$request", '-f', "sdk=$Sdk") | Out-Host
    $deadline = [DateTime]::UtcNow.AddMinutes(150)
    $run = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if (!$run) {
            $runs = (Invoke-Gh @('run', 'list', '--repo', $repository, '--workflow', 'mac-release.yml', '--limit', '100', '--json', 'databaseId,displayTitle,headSha')) -join "`n" | ConvertFrom-Json
            $run = $runs | Where-Object { $_.displayTitle -eq $title } | Select-Object -First 1
            if ($run -and $run.headSha -ne $Context.Sha) { throw 'Mac workflow used a different commit; its artifacts will not be packaged.' }
        }
        if ($run) {
            $status = (Invoke-Gh @('run', 'view', "$($run.databaseId)", '--repo', $repository, '--json', 'status,conclusion,url')) -join "`n" | ConvertFrom-Json
            Write-Host "Mac release: $($status.status) $($status.url)"
            if ($status.status -eq 'completed') {
                if ($status.conclusion -ne 'success') { throw "Mac release failed ($($status.conclusion)). See $($status.url)" }
                break
            }
        } else { Write-Host 'Waiting for GitHub to create the Mac release run...' }
        Start-Sleep -Seconds 20
    }
    if (!$run -or $status.status -ne 'completed') { throw 'Timed out waiting for Mac signing. Check GitHub Actions; the remote run was not cancelled.' }
    $stage = Join-Path $repoRoot "artifacts/mac-download-$request"
    $artifactName = "CloneDeploy-Mac-2.0.$Version.0"
    Invoke-Gh @('run', 'download', "$($run.databaseId)", '--repo', $repository, '--name', $artifactName, '--dir', $stage) | Out-Host
    $manifest = Get-Content (Join-Path $stage 'release.json') -Raw | ConvertFrom-Json
    if ($manifest.Version -ne "2.0.$Version.0") { throw 'Downloaded Mac release version does not match the release plan.' }
    $files = @('CloneDeploy-Agent-macOS-arm64.pkg')
    foreach ($name in $files) {
        if (!(Test-Path (Join-Path $stage $name) -PathType Leaf) -or (Get-Item (Join-Path $stage $name)).Length -eq 0) { throw "Missing Mac artifact: $name" }
    }
    $destination = Join-Path $repoRoot 'artifacts/mac-agent'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $destination $name) -Force }
    & (Join-Path $PSScriptRoot 'Write-MacPackageManifest.ps1') -ArtifactDirectory $destination -Version $manifest.Version
    foreach ($name in $files) {
        if ((Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $destination $name) -Algorithm SHA256).Hash) {
            throw "Local Mac package verification failed; the GitHub artifact was retained: $name"
        }
    }
    # Build-Release advances release.json only after the complete release succeeds.
    Write-Host "Downloaded signed Mac release from $($status.url)"
    # Delete only this run's signed handoff after the package and its local manifest are safe.
    # A cleanup/permission failure must not invalidate an otherwise usable local release.
    try {
        $artifactPage = (Invoke-Gh @('api', "repos/$repository/actions/runs/$($run.databaseId)/artifacts?per_page=100")) -join "`n" | ConvertFrom-Json
        $signedArtifacts = @($artifactPage.artifacts | Where-Object { $_.name -ceq $artifactName -and !$_.expired })
        if ($signedArtifacts.Count -ne 1 -or $signedArtifacts[0].id -notmatch '^[1-9][0-9]*$') {
            throw 'Could not uniquely identify the signed artifact for cleanup.'
        }
        Invoke-Gh @('api', '--method', 'DELETE', "repos/$repository/actions/artifacts/$($signedArtifacts[0].id)") | Out-Host
        Write-Host 'Deleted the temporary signed Mac artifact from GitHub.'
    } catch {
        Write-Warning "The signed Mac package is verified locally, but its temporary GitHub artifact could not be deleted. It will expire under the workflow retention policy. $($_.Exception.Message)"
    }
} finally { Pop-Location }
