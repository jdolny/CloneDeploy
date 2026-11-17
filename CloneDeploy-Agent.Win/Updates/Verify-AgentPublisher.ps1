# Shared by the build, installation launcher and detached updater. Windows verifies
# Authenticode (including timestamps); the profile EKU pins the publisher across
# Trusted Signing's daily certificate/key rotation.
function Get-AgentSigningProfile([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate) {
        throw 'A valid Authenticode signature is required.'
    }
    $profiles = @($signature.SignerCertificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        ForEach-Object { $_.EnhancedKeyUsages } |
        Where-Object { $_.Value -like '1.3.6.1.4.1.311.97.*' -and $_.Value -ne '1.3.6.1.4.1.311.97.1.0' } |
        ForEach-Object { $_.Value })
    if ($profiles.Count -ne 1) { throw 'Expected one Microsoft Trusted Signing profile identity.' }
    return $profiles[0]
}

function Assert-AgentPublisher([string]$Path, [string]$ExpectedProfile) {
    if (!$ExpectedProfile -or (Get-AgentSigningProfile $Path) -cne $ExpectedProfile) {
        throw 'Agent package publisher does not match the approved signing profile.'
    }
}
