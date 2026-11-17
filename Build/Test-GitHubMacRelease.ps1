[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = Join-Path $temporaryRoot ('clonedeploy-mac-release-tests-' + [guid]::NewGuid().ToString('N'))
$fixtureBuild = Join-Path $fixture 'Build'
$packageName = 'CloneDeploy-Agent-macOS-arm64.pkg'
$passed = 0
$testState = @{ Scenario = ""; Request = ""; Deleted = 0 }

# All GitHub traffic is mocked; downloads and packaging use an isolated temporary tree.
function gh {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'workflow') {
        $testState.Request = ($arguments | Where-Object { $_ -like 'request_id=*' }) -replace '^request_id=', ''
    } elseif ($arguments[0] -eq 'run' -and $arguments[1] -eq 'list') {
        ConvertTo-Json -InputObject @(@{ databaseId = 123; displayTitle = "Mac release 42 $($testState.Request)"; headSha = 'test-sha' })
    } elseif ($arguments[0] -eq 'run' -and $arguments[1] -eq 'view') {
        '{"status":"completed","conclusion":"success","url":"https://github.com/test/project/actions/runs/123"}'
    } elseif ($arguments[0] -eq 'run' -and $arguments[1] -eq 'download') {
        if ($testState.Scenario -eq 'download-failure') { $global:LASTEXITCODE = 1; return }
        $stage = $arguments[[array]::IndexOf($arguments, '--dir') + 1]
        [void](New-Item -ItemType Directory -Path $stage -Force)
        $version = if ($testState.Scenario -eq 'wrong-version') { '2.0.41.0' } else { '2.0.42.0' }
        Set-Content -LiteralPath (Join-Path $stage 'release.json') -Value (@{ Version = $version } | ConvertTo-Json) -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $stage $packageName) -Value 'test signed package' -Encoding UTF8
    } elseif ($arguments[0] -eq 'api' -and $arguments[1] -eq '--method') {
        if ($arguments[2] -ne 'DELETE' -or $arguments[3] -ne 'repos/test/project/actions/artifacts/101') { throw 'Unexpected deletion target.' }
        $local = Join-Path $fixture "artifacts/mac-agent/$packageName"
        if (!(Test-Path -LiteralPath $local) -or !(Test-Path -LiteralPath "$local.json")) { throw 'Cleanup preceded local package verification.' }
        if ($testState.Scenario -eq 'cleanup-failure') { $global:LASTEXITCODE = 1; return }
        $testState.Deleted++
    } elseif ($arguments[0] -eq 'api' -and $arguments[1] -like '*/commits/*') {
        'test-sha'
    } elseif ($arguments[0] -eq 'api' -and $arguments[1] -eq 'repos/test/project/actions/runs/123/artifacts?per_page=100') {
        $artifacts = @(@{ id = 101; name = 'CloneDeploy-Mac-2.0.42.0'; expired = $false },
                       @{ id = 102; name = 'mac-notarization-diagnostics'; expired = $false })
        if ($testState.Scenario -eq 'ambiguous-artifacts') { $artifacts += @{ id = 103; name = 'CloneDeploy-Mac-2.0.42.0'; expired = $false } }
        @{ artifacts = $artifacts } | ConvertTo-Json -Depth 4
    } else { throw "Unexpected GitHub CLI invocation: $($arguments -join ' ')" }
}

try {
    [void](New-Item -ItemType Directory -Path $fixtureBuild -Force)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Get-GitHubMacRelease.ps1') -Destination $fixtureBuild
    foreach ($scenarioName in @('success', 'download-failure', 'wrong-version', 'manifest-failure', 'local-copy-corruption', 'cleanup-failure', 'ambiguous-artifacts')) {
        $testState.Scenario = $scenarioName
        $testState.Deleted = 0
        $testState.Request = ''
        $writer = Join-Path $fixtureBuild 'Write-MacPackageManifest.ps1'
        if ($scenarioName -eq 'manifest-failure') { Set-Content -LiteralPath $writer -Value "throw 'Simulated manifest failure'" -Encoding UTF8 }
        else { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Write-MacPackageManifest.ps1') -Destination $writer -Force }
        if ($scenarioName -eq 'local-copy-corruption') { Add-Content -LiteralPath $writer -Value 'Add-Content -LiteralPath $package -Value corrupt' -Encoding UTF8 }
        $failure = $null
        $warnings = @()
        try {
            & (Join-Path $fixtureBuild 'Get-GitHubMacRelease.ps1') -Context ([pscustomobject]@{ Repository = 'test/project'; Branch = 'main'; Sha = 'test-sha' }) -Version 42 -Sdk '10.0.x' -WarningVariable warnings -WarningAction SilentlyContinue
        } catch { $failure = $_.Exception.Message }
        $shouldFail = $scenarioName -in @('download-failure', 'wrong-version', 'manifest-failure', 'local-copy-corruption')
        if ($shouldFail -ne ($null -ne $failure)) { throw "Unexpected result for ${scenarioName}: $failure" }
        $expectedDeletes = if ($scenarioName -eq 'success') { 1 } else { 0 }
        if ($testState.Deleted -ne $expectedDeletes) { throw "Incorrect cleanup count for $scenarioName" }
        if ($scenarioName -in @('cleanup-failure', 'ambiguous-artifacts') -and !$warnings.Count) { throw 'Cleanup failure must warn without failing the release.' }
        $passed++
    }
    Write-Host "Passed $passed Mac artifact handoff/cleanup tests."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'clonedeploy-mac-release-tests-*') { throw 'Invalid temporary cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
