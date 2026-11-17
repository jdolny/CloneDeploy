[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path,
      [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
      [Parameter(Mandatory)][string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MsiDatabase.ps1')
$script:Installer = New-Object -ComObject WindowsInstaller.Installer
try {
    $db = $script:Installer.OpenDatabase((Resolve-Path -LiteralPath $Path).Path, 0)
    try {
        $version = Get-MsiValue $db 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion'''
        $upgradeCode = Get-MsiValue $db 'SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode'''
        $expectedCode = if ($Architecture -eq 'arm64') { '{620CDB5F-90AA-4381-9FAA-6546CBC2E350}' } else { '{D75325AF-F72B-4039-9B5D-DBAA9E8AB0F5}' }
        $summary = $db.SummaryInformation(0)
        try { $platform = ($summary.Property(7) -split ';')[0] }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
        if ($platform -ine $Architecture -or $upgradeCode -ine $expectedCode -or $version -ne $ExpectedVersion) {
            throw 'MSI version, architecture or upgrade identity does not match the requested build.'
        }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) }
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($script:Installer) }
$manifest = [ordered]@{
    SchemaVersion = 1; Version = $version; Architecture = $Architecture; UpgradeCode = $upgradeCode
    Size = (Get-Item -LiteralPath $Path).Length; Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText("$Path.json", ($manifest | ConvertTo-Json) + [Environment]::NewLine)
