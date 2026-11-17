# Installer-only helpers. No plaintext credentials are written to disk or logs.
function New-SetupPassword {
    $bytes = New-Object byte[] 48
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    # Guarantee Windows complexity categories as well as 384 random bits.
    return 'Aa1!' + [Convert]::ToBase64String($bytes)
}

function Get-SetupCertificate([string]$Thumbprint) {
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) { throw 'The installation encryption certificate is not configured.' }
    $certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$Thumbprint" -ErrorAction Stop
    if (!$certificate.HasPrivateKey) { throw 'The installation encryption certificate has no private key.' }
    # This is an encryption key, not a TLS identity. Expiration must not strand data.
    return $certificate
}

function Grant-SetupCertificateAccess($Certificate, [string]$ServiceSid) {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    try {
        if ($rsa -isnot [Security.Cryptography.RSACng]) { throw 'Expected a CNG installation encryption key.' }
        $keyPath = Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\' + $rsa.Key.UniqueName)
        $acl = New-Object Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'Allow')))
        }
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($ServiceSid)), 'Read', 'Allow')))
        Set-Acl -LiteralPath $keyPath -AclObject $acl
    } finally { if ($rsa) { $rsa.Dispose() } }
}

function Protect-SetupDatabasePassword([string]$Password, $Certificate) {
    Add-Type -AssemblyName System.Security
    $content = New-Object Security.Cryptography.Pkcs.ContentInfo(,[Text.Encoding]::UTF8.GetBytes($Password))
    $algorithm = New-Object Security.Cryptography.Pkcs.AlgorithmIdentifier((New-Object Security.Cryptography.Oid('2.16.840.1.101.3.4.1.42')))
    $message = New-Object Security.Cryptography.Pkcs.EnvelopedCms($content, $algorithm)
    $message.Encrypt((New-Object Security.Cryptography.Pkcs.CmsRecipient($Certificate)))
    return [Convert]::ToBase64String($message.Encode())
}

function Unprotect-SetupDatabasePassword([string]$EncryptedPassword, $Certificate) {
    Add-Type -AssemblyName System.Security
    $message = New-Object Security.Cryptography.Pkcs.EnvelopedCms
    $message.Decode([Convert]::FromBase64String($EncryptedPassword))
    $certificates = New-Object Security.Cryptography.X509Certificates.X509Certificate2Collection
    [void]$certificates.Add($Certificate)
    $message.Decrypt($certificates)
    return [Text.Encoding]::UTF8.GetString($message.ContentInfo.Content)
}
