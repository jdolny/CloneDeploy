param([Parameter(Mandatory=$true)][int]$DiskNumber)
$ErrorActionPreference = 'Stop'

function Get-CaptureBootEntries([string]$text) {
    # Values and identifiers are stable even when BCDEdit's field labels are localized.
    foreach ($block in [regex]::Split($text.Trim(), '\r?\n\s*\r?\n')) {
        $identifier = [regex]::Match($block, '(?m)^\S+\s+(\{[0-9a-fA-F-]{36}\})\s*$')
        if (-not $identifier.Success) { continue }
        $loader = $block -match '(?m)^\S+\s+CloneDeploy Imaging\s*$' -and
            $block -match '(?i)\\CloneDeployCapture\\boot\.wim[,\s]'
        $ramdisk = $block -match '(?m)^\S+\s+CloneDeploy WinPE ramdisk\s*$' -and
            $block -match '(?im)^\S+\s+\\CloneDeployCapture\\boot\.sdi\s*$'
        if ($loader -or $ramdisk) {
            [pscustomobject]@{ Id = ([guid]$identifier.Groups[1].Value).ToString('B'); Loader = $loader }
        }
    }
}

try {
    if (-not (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\MiniNT')) { throw 'Capture boot cleanup requires WinPE.' }
    $partitions = @(Get-Partition -DiskNumber $DiskNumber | Where-Object {
        $_.GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -or $_.IsActive
    })
    $stores = 0
    foreach ($partition in $partitions) {
        $mount = $null
        try {
            $root = @($partition.AccessPaths | Where-Object { $_ -match '^[A-Za-z]:\\$' } | Select-Object -First 1)
            if ($root.Count) { $root = $root[0] }
            else {
                $used = [IO.Directory]::GetLogicalDrives()
                $root = @('C','D','E','F','G','H','I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','Y','Z') |
                    ForEach-Object { $_ + ':\' } | Where-Object { $_ -notin $used } | Select-Object -First 1
                if (-not $root) { throw 'No drive letter is available for capture boot cleanup.' }
                Add-PartitionAccessPath -DiskNumber $DiskNumber -PartitionNumber $partition.PartitionNumber -AccessPath $root
                $mount = $root
            }
            foreach ($relative in @('EFI\Microsoft\Boot\BCD', 'Boot\BCD')) {
                $store = Join-Path $root $relative
                if (-not (Test-Path -LiteralPath $store)) { continue }
                $stores++
                $listing = & "$env:SystemRoot\System32\bcdedit.exe" /store $store /enum all /v 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Could not read capture BCD store: $store" }
                $entries = @(Get-CaptureBootEntries ($listing -join "`n") | Sort-Object Loader -Descending)
                foreach ($entry in $entries) {
                    & "$env:SystemRoot\System32\bcdedit.exe" /store $store /delete $entry.Id /cleanup
                    if ($LASTEXITCODE -ne 0) { throw "Could not remove capture boot entry $($entry.Id)." }
                    Write-Output "Removed temporary capture boot entry $($entry.Id) from $store."
                }
                if (-not $entries.Count) { Write-Output "No temporary capture boot entries in $store." }
            }
        }
        finally {
            if ($mount) { Remove-PartitionAccessPath -DiskNumber $DiskNumber -PartitionNumber $partition.PartitionNumber -AccessPath $mount }
        }
    }
    if (-not $stores) { throw 'No boot configuration store was found on the prepared capture disk.' }
}
catch { Write-Error $_ -ErrorAction Continue; exit 1 }
