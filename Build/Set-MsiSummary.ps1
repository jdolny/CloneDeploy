[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../CloneDeploy-Agent.Win/Packaging/MsiDatabase.ps1')
$installer = New-Object -ComObject WindowsInstaller.Installer
try {
    $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Path), 1)
    try {
        $name = Get-MsiValue $database 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductName'''
        $version = Get-MsiValue $database 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion'''
        if (!$name -or !$version) { throw 'MSI product name and version are required.' }
        $summary = $database.SummaryInformation(1)
        try {
            $platform = ($summary.Property(7) -split ';')[0]
            $architecture = switch ($platform) {
                'x64' { 'x64' }
                'Arm64' { 'ARM64' }
                default { throw "Unsupported MSI platform: $platform" }
            }
            # The ARM64 agent already includes its architecture in ProductName.
            $name = $name -replace ' \((ARM64|x64)\)$', ''
            $summary.Property(3) = "$name $version ($architecture)"
            [void]$summary.Persist()
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
        [void]$database.Commit()
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
