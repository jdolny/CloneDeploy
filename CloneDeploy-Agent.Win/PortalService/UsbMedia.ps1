$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Get-UsbTargets {
    $pageDrives = @(Get-CimInstance Win32_PageFileUsage | ForEach-Object { $_.Name.Substring(0, 1) })
    foreach ($disk in @(Get-Disk)) {
        if ($disk.BusType -notin @('USB', 'SD', 'MMC') -or $disk.IsBoot -or $disk.IsSystem -or
            $disk.IsOffline -or $disk.IsReadOnly -or $disk.Size -lt 1GB -or [string]::IsNullOrWhiteSpace($disk.UniqueId)) { continue }
        $parts = @(Get-Partition | Where-Object DiskNumber -eq $disk.Number)
        if (@($parts | Where-Object { $_.IsBoot -or $_.IsSystem -or [string]$_.DriveLetter -in $pageDrives }).Count) { continue }
        $volumes = @($parts | Where-Object DriveLetter | ForEach-Object { "$($_.DriveLetter):" }) -join ', '
        [pscustomobject]@{ Number = [int]$disk.Number; UniqueId = [string]$disk.UniqueId; SerialNumber = [string]$disk.SerialNumber;
            Name = [string]$disk.FriendlyName; Size = [long]$disk.Size; Volumes = $volumes }
    }
}
function Assert-UsbTarget($expected) {
    $matches = @(Get-UsbTargets | Where-Object { $_.Number -eq $expected.Number -and
        $_.UniqueId -ceq $expected.UniqueId -and $_.SerialNumber -ceq $expected.SerialNumber -and
        $_.Name -ceq $expected.Name -and $_.Size -eq $expected.Size -and $_.Volumes -ceq $expected.Volumes })
    if ($matches.Count -ne 1) { throw 'The selected disk changed or is no longer eligible. Refresh drives and select it again.' }
    $disk = Get-Disk -Number $expected.Number
    if ($disk.UniqueId -cne $expected.UniqueId -or $disk.IsBoot -or $disk.IsSystem -or
        $disk.BusType -notin @('USB', 'SD', 'MMC') -or $disk.IsOffline -or $disk.IsReadOnly) { throw 'Disk safety check failed.' }
    return $disk
}
function Write-UsbMedia($inputData) {
    $iso = $inputData.Iso
    $architecture = if ($inputData.Architecture) { [string]$inputData.Architecture } else { 'x64' }
    if ($architecture -cnotin @('x64', 'arm64')) { throw 'Unsupported USB architecture.' }
    try {
        $image = Mount-DiskImage -ImagePath $iso -Access ReadOnly -PassThru
        $volumes = @($image | Get-Volume)
        if ($volumes.Count -ne 1 -or !$volumes[0].DriveLetter) { throw 'Could not mount the downloaded ISO.' }
        $source = "$($volumes[0].DriveLetter):\"
        $requiredFiles = if ($architecture -eq 'arm64') { @('EFI\Boot\bootaa64.efi', 'EFI\Microsoft\Boot\BCD', 'sources\boot.wim', 'boot\boot.sdi') }
            else { @('EFI\Boot\bootx64.efi', 'EFI\Microsoft\Boot\BCD', 'sources\boot.wim', 'boot\BCD', 'boot\boot.sdi', 'bootmgr') }
        foreach ($required in $requiredFiles) {
            if (!(Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) { throw "The ISO is missing $required." }
        }
        $files = @(Get-ChildItem -LiteralPath $source -Recurse -File -Force)
        if (@($files | Where-Object { $_.Length -ge 4GB }).Count) { throw 'This ISO contains a file of 4 GB or more. Rebuild a smaller WinPE boot environment for FAT32 USB boot.' }
        if (@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'ISO links are not supported.' }
        $requiredSize = ($files | Measure-Object Length -Sum).Sum + 256MB
        $partitionSize = [math]::Floor([math]::Min(30GB, ([long]$inputData.Drive.Size - 16MB)) / 1MB) * 1MB
        if ($partitionSize -lt $requiredSize) { throw 'The selected disk does not have enough room for this WinPE image.' }
        $bootsect = Join-Path $source 'boot\bootsect.exe'
        if (!(Test-Path -LiteralPath $bootsect)) { $bootsect = Join-Path $env:SystemRoot 'System32\bootsect.exe' }
        $hasBios = $architecture -eq 'x64' -and (Test-Path -LiteralPath $bootsect -PathType Leaf)
        # Recheck the complete identity and protected volumes immediately before erasing.
        $disk = Assert-UsbTarget $inputData.Drive
        $isoDrive = [IO.Path]::GetPathRoot($iso).TrimEnd('\').TrimEnd(':')
        if ($isoDrive.Length -eq 1 -and @(Get-Partition | Where-Object { [string]$_.DriveLetter -eq $isoDrive -and $_.DiskNumber -eq $disk.Number }).Count) {
            throw 'The selected disk contains the downloaded ISO. Choose a different external disk.'
        }
        $devicePath = $disk.Path
        $disk | Clear-Disk -RemoveData -RemoveOEM -Confirm:$false
        $disk = Get-Disk -Number $inputData.Drive.Number
        if ($disk.Path -cne $devicePath -or $disk.SerialNumber -cne $inputData.Drive.SerialNumber -or
            $disk.Size -ne $inputData.Drive.Size -or $disk.IsBoot -or $disk.IsSystem) { throw 'Disk identity changed after clearing.' }
        if ($disk.PartitionStyle -eq 'RAW') { $disk | Initialize-Disk -PartitionStyle MBR | Out-Null }
        elseif ($disk.PartitionStyle -ne 'MBR') { $disk | Set-Disk -PartitionStyle MBR }
        $partition = New-Partition -DiskNumber $disk.Number -Size $partitionSize -AssignDriveLetter -IsActive
        $partition | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'CloneDeploy' -Confirm:$false -Force | Out-Null
        $partition = Get-Partition -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber
        if (!$partition.DriveLetter) { throw 'Windows did not assign a USB drive letter.' }
        $target = "$($partition.DriveLetter):\"
        foreach ($entry in @(Get-ChildItem -LiteralPath $source -Force)) {
            Copy-Item -LiteralPath $entry.FullName -Destination $target -Recurse -Force
        }
        if ($hasBios) {
            & $bootsect /nt60 "$($partition.DriveLetter):" /mbr /force | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not install BIOS boot code (exit $LASTEXITCODE)." }
        }
        # Read every copied file back before reporting success.
        foreach ($file in $files) {
            $copied = Join-Path $target $file.FullName.Substring($source.Length)
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash) { throw 'USB verification failed. Try a different drive.' }
        }
        if ($architecture -eq 'arm64') { 'Created and verified bootable USB for ARM64 UEFI.' }
        elseif ($hasBios) { 'Created and verified bootable USB for x64 UEFI and legacy BIOS.' }
        else { 'Created and verified bootable USB for x64 UEFI. Legacy BIOS is unavailable because bootsect.exe was not found.' }
    }
    finally { Dismount-DiskImage -ImagePath $iso -ErrorAction SilentlyContinue | Out-Null }
}
