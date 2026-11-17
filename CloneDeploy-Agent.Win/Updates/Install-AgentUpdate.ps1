param(
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedHash,
    [Parameter(Mandatory=$true)][string]$TargetVersion,
    [Parameter(Mandatory=$true)][string]$PublisherPath,
    [Parameter(Mandatory=$true)][string]$LogDirectory
)
$ErrorActionPreference = 'Stop'
$result = @{ TargetVersion = $TargetVersion; PackageHash = $ExpectedHash; ExitCode = -1; CompletedAtUtc = $null; RebootRequired = $false; Error = $null }
try {
    . (Join-Path $PSScriptRoot 'Verify-AgentPublisher.ps1')
    $profile = Get-AgentSigningProfile $PublisherPath
    Assert-AgentPublisher $Package $profile
    if ((Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash -ne $ExpectedHash) { throw 'Agent update hash verification failed.' }
    $log = Join-Path $LogDirectory 'install.log'
    # This process lives outside the agent and its install directory. MSI owns stop/start and rollback.
    # Restart registered per-user portals after replacement; /norestart suppresses machine reboot only.
    $arguments = '/i "' + $Package + '" /qn /norestart REBOOT=ReallySuppress MSIRMSHUTDOWN=0 MSIDISABLERMRESTART=0 /L*v "' + $log + '"'
    $msi = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    $result.ExitCode = $msi.ExitCode
    $result.RebootRequired = $msi.ExitCode -eq 3010
    if ($msi.ExitCode -eq 0 -or $msi.ExitCode -eq 3010) {
        $cooldown = Join-Path $LogDirectory ('retry-after-' + $ExpectedHash.ToUpperInvariant() + '.txt')
        if (Test-Path -LiteralPath $cooldown) { Remove-Item -LiteralPath $cooldown -Force }
    }
} catch { $result.Error = $_.Exception.Message }
finally {
    $result.CompletedAtUtc = [DateTime]::UtcNow.ToString('O')
    # Diagnostics must not prevent service recovery, including when this file is locked.
    try { $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $LogDirectory 'update-result.json') -Encoding UTF8 }
    catch { Write-Warning ('Could not record update result: ' + $_.Exception.Message) }
    # Also restore service availability after a failed installation/rollback.
    try { Start-Service -Name 'CloneDeployAgent' -ErrorAction Stop } catch { }
}
exit $result.ExitCode
