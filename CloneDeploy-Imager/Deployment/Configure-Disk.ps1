param([Parameter(Mandatory=$true)][string]$RequestFile)
$ErrorActionPreference = 'Stop'
try {
    if (-not (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\MiniNT')) { throw 'Disk deployment requires WinPE.' }
    $request = Get-Content -LiteralPath $RequestFile -Raw | ConvertFrom-Json
    $disk = Get-Disk -Number ([uint32]$request.Number)
    if ($disk.UniqueId.Trim() -ne $request.UniqueId -or $disk.Size -ne [uint64]$request.Size -or $disk.IsOffline -or $disk.IsReadOnly -or $disk.IsBoot -or $disk.IsSystem) { throw 'Target disk identity or eligibility changed.' }
    # Get-Disk may expose the bus type as a display name rather than its numeric CIM value.
    # Keep this allowlist aligned with DeploymentDisk.Eligible; unknown and removable buses remain excluded.
    if ([string]$disk.BusType -notin @('1','SCSI','3','ATA','8','RAID','10','SAS','11','SATA','14','Virtual','17','NVMe','18','SCM')) { throw 'Target is not an eligible internal disk.' }
    foreach ($letter in @($request.WindowsLetter, $request.SystemLetter, $request.RecoveryLetter)) {
        if ($letter -notmatch '^[C-WYZ]$') { throw 'Invalid deployment drive letter.' }
    }
    $windows = "$($request.WindowsLetter):"
    $system = "$($request.SystemLetter):"
    $recovery = "$($request.RecoveryLetter):"
    function Invoke-DiskPart([string[]]$commands) {
        $scriptPath = Join-Path (Split-Path -Parent $RequestFile) 'diskpart.txt'
        Set-Content -LiteralPath $scriptPath -Value ($commands + 'exit') -Encoding Ascii
        & "$env:SystemRoot\System32\diskpart.exe" /s $scriptPath
        if ($LASTEXITCODE -ne 0) { throw 'DiskPart failed.' }
    }
    function Get-DeploymentMount([string]$root) {
        if (-not ('CloneDeployVolumeNative' -as [type])) {
            Add-Type @'
using System.Runtime.InteropServices;
using System.Text;
public static class CloneDeployVolumeNative {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformation(string root, StringBuilder label, uint labelLength,
        out uint serial, out uint componentLength, out uint flags, StringBuilder fileSystem, uint fileSystemLength);
}
'@
        }
        $volumePathBufferCharacters = 1024
        $volumeMetadataBufferCharacters = 261 # Includes space for the terminating null.
        $name = New-Object System.Text.StringBuilder $volumePathBufferCharacters
        if (-not [CloneDeployVolumeNative]::GetVolumeNameForVolumeMountPoint($root, $name, $name.Capacity)) {
            return [pscustomobject]@{ VolumePath = ''; FileSystem = ''; Accessible = $false; ErrorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error() }
        }
        $label = New-Object System.Text.StringBuilder $volumeMetadataBufferCharacters
        $format = New-Object System.Text.StringBuilder $volumeMetadataBufferCharacters
        [uint32]$serial = 0; [uint32]$componentLength = 0; [uint32]$flags = 0
        $errorCode = 0
        if (-not [CloneDeployVolumeNative]::GetVolumeInformation($root, $label, $label.Capacity,
            [ref]$serial, [ref]$componentLength, [ref]$flags, $format, $format.Capacity)) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        }
        return [pscustomobject]@{ VolumePath = $name.ToString(); FileSystem = $format.ToString(); Accessible = [IO.Directory]::Exists($root); ErrorCode = $errorCode }
    }
    function Get-DeploymentPartition([string]$letter, [string]$fileSystem = '') {
        $maximumAttempts = 10
        $retryDelayMilliseconds = 500
        $root = "${letter}:\"
        # DiskPart changes can precede Storage CIM cache updates. EFI partitions may
        # expose a temporary mount in AccessPaths without populating DriveLetter.
        for ($attempt = 0; $attempt -lt $maximumAttempts; $attempt++) {
            Update-HostStorageCache
            $partitions = @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue)
            $matches = @($partitions | Where-Object { $_.DriveLetter -eq $letter -or $_.AccessPaths -contains $root })
            $mount = Get-DeploymentMount -root $root
            # WinPE can omit an EFI mount from BOTH DriveLetter and AccessPaths.
            # Ask the mount manager what volume the letter actually names, then
            # correlate that GUID with a partition on this specific target disk.
            if ($matches.Count -eq 0 -and $mount.VolumePath) {
                $matches = @($partitions | Where-Object { $_.AccessPaths -contains $mount.VolumePath })
            }
            if ($matches.Count -gt 1) { throw "Multiple target partitions claim $root" }
            if ($matches.Count -eq 1) {
                $partition = $matches[0]
                if ($partition.DiskNumber -ne $disk.Number) { throw 'Partition is on the wrong disk.' }
                $volumePaths = @($partition.AccessPaths | Where-Object { $_.StartsWith('\\?\Volume{', [StringComparison]::OrdinalIgnoreCase) })
                if ($mount.VolumePath -and $volumePaths.Count -gt 0 -and $volumePaths -notcontains $mount.VolumePath) {
                    throw "Mount $root points to a different volume than the target partition."
                }
                # Query the mounted filesystem directly; Storage CIM can omit EFI
                # volumes from Get-Volume as well as omitting their drive letters.
                if ($mount.VolumePath -and $mount.ErrorCode -eq 0 -and $mount.FileSystem -and
                    (-not $fileSystem -or $mount.FileSystem -eq $fileSystem) -and
                    $mount.Accessible) {
                    return $partition
                }
            }
            if ($attempt -lt ($maximumAttempts - 1)) { Start-Sleep -Milliseconds $retryDelayMilliseconds }
        }
        $observed = ($partitions | ForEach-Object {
            "partition $($_.PartitionNumber): letter=$($_.DriveLetter), paths=$($_.AccessPaths -join ',')"
        }) -join '; '
        throw "Partition layout verification failed for $root on disk $($disk.Number) (expected filesystem: $fileSystem). Mount volume=$($mount.VolumePath), filesystem=$($mount.FileSystem), Win32 error=$($mount.ErrorCode). Observed: $observed"
    }
    function Get-DeploymentDriveLetters { [IO.Directory]::GetLogicalDrives() }
    function Invoke-DeploymentBcdEdit([string[]]$arguments) {
        & "$env:SystemRoot\System32\bcdedit.exe" @arguments
        if ($LASTEXITCODE -ne 0) { throw "BCDEdit failed (exit $LASTEXITCODE): $($arguments -join ' ')" }
    }
    function Register-DeploymentFirmwareBoot([string]$systemRoot) {
        # BCDBoot /s copies files but deliberately skips NVRAM registration.
        # Select the verified target ESP as the system store for this WinPE session;
        # /store alone edits an offline file and does not configure firmware.
        Write-Output "Registering Windows Boot Manager on $systemRoot in UEFI firmware."
        Invoke-DeploymentBcdEdit -arguments @('/sysstore', $systemRoot)
        Invoke-DeploymentBcdEdit -arguments @('/set', '{bootmgr}', 'device', "partition=$systemRoot")
        Invoke-DeploymentBcdEdit -arguments @('/set', '{bootmgr}', 'path', '\EFI\Microsoft\Boot\bootmgfw.efi')
        Invoke-DeploymentBcdEdit -arguments @('/set', '{fwbootmgr}', 'displayorder', '{bootmgr}', '/addfirst')
        Invoke-DeploymentBcdEdit -arguments @('/enum', 'firmware')
    }
    function Copy-FfuRecovery([string]$source, [string]$destination) {
        # Copy the whole recovery payload, including arbitrary root files and folders.
        # Volume bookkeeping is recreated by Windows and does not belong in the payload.
        # Preserve links as links rather than traversing them into another volume.
        $excluded = @(
            (Join-Path $source 'System Volume Information'), (Join-Path $source '$RECYCLE.BIN'),
            (Join-Path $destination 'System Volume Information'), (Join-Path $destination '$RECYCLE.BIN')
        )
        & "$env:SystemRoot\System32\robocopy.exe" $source $destination /E /COPYALL /DCOPY:DAT /B /SL /SJ /R:0 /W:0 /NFL /NDL /XD $excluded
        if ($LASTEXITCODE -ge 8) { throw "Recovery file copy failed (Robocopy exit $LASTEXITCODE)." }
    }
    function Find-FfuWinRe([string]$windowsRoot, [string]$recoveryRoot) {
        foreach ($path in @("$recoveryRoot\Recovery\WindowsRE\Winre.wim", "$windowsRoot\Windows\System32\Recovery\Winre.wim")) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
        }
        return $null
    }
    function Expand-FfuWindows([object]$osPartition, [string]$windowsRoot, [string]$recoveryRoot) {
        # Only the verified Windows partition and a single trailing WinRE partition
        # can be changed. OEM/data partitions are never deleted to reclaim space.
        Update-HostStorageCache
        $layout = @(Get-Partition -DiskNumber $disk.Number | Sort-Object Offset)
        $osPartition = Get-Partition -DiskNumber $disk.Number -PartitionNumber $osPartition.PartitionNumber
        $after = @($layout | Where-Object { $_.Offset -gt $osPartition.Offset })
        if ($after.Count -eq 0) {
            $maximum = (Get-PartitionSupportedSize -DiskNumber $disk.Number -PartitionNumber $osPartition.PartitionNumber).SizeMax
            if ($maximum -gt $osPartition.Size + 1MB) {
                Write-Host 'Expanding the final Windows partition into adjacent free space.'
                Resize-Partition -DiskNumber $disk.Number -PartitionNumber $osPartition.PartitionNumber -Size $maximum | Out-Null
                if ((Get-Partition -DiskNumber $disk.Number -PartitionNumber $osPartition.PartitionNumber).Size -lt $maximum) { throw 'Windows partition expansion verification failed.' }
            } else { Write-Host 'FFU Windows partition already uses the available space.' }
            return $null
        }
        $recoveryType = '{de94bba4-06d1-4d40-a16a-bfd50179d6ac}'
        if ($after.Count -ne 1 -or $after[0].GptType -ne $recoveryType -or
            @($layout | Where-Object { $_.GptType -eq $recoveryType }).Count -ne 1) {
            Write-Host 'WARNING: FFU expansion skipped: partitions after Windows are not a single Windows recovery partition.'
            return $null
        }
        $old = $after[0]
        # Ignore alignment/GPT tail space and avoid rebuilding already-expanded images.
        if ($disk.Size - ($old.Offset + $old.Size) -lt 16MB) {
            Write-Host 'FFU layout already uses the available disk space.'
            return $null
        }
        if ((Get-DeploymentDriveLetters) -contains "$recoveryRoot\") { throw 'Recovery staging drive letter is occupied.' }
        $mounted = $false
        $deleted = $false
        $backup = $null
        try {
            Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $old.PartitionNumber -AccessPath "$recoveryRoot\"
            $mounted = $true
            $verified = Get-DeploymentPartition -letter $recoveryRoot[0] -fileSystem 'NTFS'
            if ($verified.PartitionNumber -ne $old.PartitionNumber) { throw 'Recovery mount points to the wrong partition.' }
            $winRe = Find-FfuWinRe -windowsRoot $windowsRoot -recoveryRoot $recoveryRoot
            if (-not $winRe) {
                Write-Host 'WARNING: Winre.wim is missing; Windows will still be expanded, but recovery will be unavailable.'
            } else {
                Write-Host "FFU recovery image found at $winRe"
            }
            $stageWinRe = $winRe -and $winRe -ne "$recoveryRoot\Recovery\WindowsRE\Winre.wim"
            $recoveryVolume = Get-Volume -DriveLetter $recoveryRoot[0]
            [long]$contentBytes = $recoveryVolume.Size - $recoveryVolume.SizeRemaining
            if ($stageWinRe) { $contentBytes += (Get-Item -LiteralPath $winRe -Force).Length }
            # Retain at least the original capacity, with room for WinRE servicing.
            [long]$reserve = [Math]::Ceiling([Math]::Max([Math]::Max($old.Size, 2GB), $contentBytes + 300MB) / 1MB) * 1MB
            if ($disk.Size - $old.Offset - $reserve -lt 16MB) {
                Write-Host 'WARNING: FFU expansion skipped: not enough extra space to grow Windows and preserve recovery capacity.'
                return $null
            }
            $osVolume = Get-Volume -DriveLetter $windowsRoot[0]
            $backup = Join-Path "$windowsRoot\" ('CloneDeploy-Recovery-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $backup -ErrorAction Stop | Out-Null
            Write-Host "Preserving Windows recovery contents in $backup"
            $recoveryPreserved = $false
            try {
                if ($osVolume.SizeRemaining -lt $contentBytes + 64MB) { throw 'Insufficient Windows free space to preserve recovery files.' }
                Copy-FfuRecovery -source "$recoveryRoot\" -destination $backup | Out-Host
                if ($stageWinRe) {
                    $stagedRecovery = Join-Path $backup 'Recovery\WindowsRE'
                    New-Item -ItemType Directory -Path $stagedRecovery -Force -ErrorAction Stop | Out-Null
                    Copy-Item -LiteralPath $winRe -Destination (Join-Path $stagedRecovery 'Winre.wim') -Force -ErrorAction Stop
                }
                $recoveryPreserved = $true
            } catch {
                Write-Host "WARNING: Recovery files could not be preserved; continuing Windows expansion without recovery: $_"
            }
            # Every destructive operation is scoped to the identified target partition.
            Invoke-DiskPart @("select disk $($disk.Number)", "select partition $($old.PartitionNumber)", 'delete partition override') | Out-Host
            $deleted = $true
            Update-HostStorageCache
            if (Get-Partition -DiskNumber $disk.Number -PartitionNumber $old.PartitionNumber -ErrorAction SilentlyContinue) { throw 'Recovery partition deletion did not complete.' }
            [long]$maximum = (Get-PartitionSupportedSize -DiskNumber $disk.Number -PartitionNumber $osPartition.PartitionNumber).SizeMax
            [long]$newSize = [Math]::Floor(($maximum - $reserve) / 1MB) * 1MB
            if ($newSize -le $osPartition.Size) { throw 'The verified layout does not leave room for Windows expansion and recovery.' }
            Write-Host 'Expanding Windows and recreating the recovery partition at the end of the disk.'
            Resize-Partition -DiskNumber $disk.Number -PartitionNumber $osPartition.PartitionNumber -Size $newSize | Out-Null
            $expanded = Get-Partition -DiskNumber $disk.Number -PartitionNumber $osPartition.PartitionNumber
            if ($expanded.Size -ne $newSize) { throw 'Windows partition expansion verification failed.' }
            $created = New-Partition -DiskNumber $disk.Number -Offset ($expanded.Offset + $expanded.Size) -Size $reserve
            $created | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'Recovery' -Confirm:$false | Out-Null
            Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $created.PartitionNumber -AccessPath "$recoveryRoot\"
            $verified = Get-DeploymentPartition -letter $recoveryRoot[0] -fileSystem 'NTFS'
            if ($verified.PartitionNumber -ne $created.PartitionNumber -or $verified.Size -lt $reserve) { throw 'Recreated recovery partition verification failed.' }
            if ($recoveryPreserved) {
                try { Copy-FfuRecovery -source $backup -destination "$recoveryRoot\" | Out-Host }
                catch { Write-Host "WARNING: Windows expanded, but recovery files could not be restored: $_" }
            }
            Invoke-DiskPart @("select disk $($disk.Number)", "select partition $($created.PartitionNumber)",
                'set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac override', 'gpt attributes=0x8000000000000001') | Out-Host
            return [pscustomobject]@{ PartitionNumber = $created.PartitionNumber; Root = $recoveryRoot; Backup = $backup }
        } catch {
            if ($backup) { Write-Host "Recovery preservation files retained at $backup. Deployment finalization failed: $_" }
            throw
        } finally {
            if ($mounted -and -not $deleted) {
                Remove-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $old.PartitionNumber -AccessPath "$recoveryRoot\"
            }
        }
    }
    function Invoke-FfuReAgent([string]$windowsRoot, [string[]]$arguments) {
        & "$windowsRoot\Windows\System32\reagentc.exe" @arguments
        if ($LASTEXITCODE -ne 0) { throw "Windows recovery configuration failed (exit $LASTEXITCODE). Finalization stopped." }
    }
    function Get-FfuLoaderIdentifier([string]$bootFile) {
        $loader = & "$env:SystemRoot\System32\bcdedit.exe" /store $bootFile /enum '{default}' /v
        if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the restored Windows boot identifier.' }
        $guid = [regex]::Match(($loader -join "`n"), '\{[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}')
        if (-not $guid.Success) { throw 'The restored Windows boot identifier is missing.' }
        return $guid.Value
    }
    function Register-DeploymentRecovery([string]$windowsRoot, [string]$recoveryRoot, [string]$bootFile) {
        # BCDBoot and firmware registration must have completed before /enable /osguid.
        Invoke-FfuReAgent -windowsRoot $windowsRoot -arguments @('/setreimage', '/path', "$recoveryRoot\Recovery\WindowsRE", '/target', "$windowsRoot\Windows")
        $guid = Get-FfuLoaderIdentifier -bootFile $bootFile
        Invoke-FfuReAgent -windowsRoot $windowsRoot -arguments @('/enable', '/osguid', $guid)
    }
    function Register-WimRecovery([string]$windowsRoot, [string]$recoveryRoot, [string]$bootFile, [bool]$uefi) {
        $partition = Get-DeploymentPartition -letter $recoveryRoot[0] -fileSystem 'NTFS'
        if ($partition.DiskNumber -ne $disk.Number) { throw 'Recovery partition is on the wrong disk.' }
        if (Test-Path -LiteralPath "$windowsRoot\Windows\System32\Recovery\Winre.wim") {
            New-Item -ItemType Directory -Path "$recoveryRoot\Recovery\WindowsRE" -Force | Out-Null
            Copy-Item -LiteralPath "$windowsRoot\Windows\System32\Recovery\Winre.wim" -Destination "$recoveryRoot\Recovery\WindowsRE\Winre.wim"
            Register-DeploymentRecovery -windowsRoot $windowsRoot -recoveryRoot $recoveryRoot -bootFile $bootFile
        } else {
            Write-Host 'WARNING: The WIM contains no Winre.wim; its recovery partition will remain empty and WinRE cannot be enabled.'
        }
        $commands = @("select disk $($disk.Number)", "select partition $($partition.PartitionNumber)")
        if ($uefi) { $commands += @('set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac','gpt attributes=0x8000000000000001') }
        else { $commands += 'set id=27' }
        $commands += "remove letter=$($recoveryRoot[0])"
        Invoke-DiskPart $commands
    }
    function Register-FfuRecovery([object]$state, [string]$windowsRoot, [string]$bootFile) {
        if (Test-Path -LiteralPath "$($state.Root)\Recovery\WindowsRE\Winre.wim" -PathType Leaf) {
            try {
                Register-DeploymentRecovery -windowsRoot $windowsRoot -recoveryRoot $state.Root -bootFile $bootFile
                Write-Host 'Windows recovery files restored, registered, and enabled.'
            } catch { Write-Host "WARNING: Windows expanded, but recovery registration failed: $_" }
        } else { Write-Host 'WARNING: Windows expanded; WinRE is unavailable because no recovery image was restored.' }
        try {
            Invoke-DiskPart @("select disk $($disk.Number)", "select partition $($state.PartitionNumber)",
                'set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac override', 'gpt attributes=0x8000000000000001',
                "remove letter=$($state.Root[0])")
            Update-HostStorageCache
            $hidden = Get-Partition -DiskNumber $disk.Number -PartitionNumber $state.PartitionNumber
            $hasDriveLetter = [string]$hidden.DriveLetter -match '^[A-Za-z]$'
            if ($hidden.GptType -ne '{de94bba4-06d1-4d40-a16a-bfd50179d6ac}' -or -not $hidden.NoDefaultDriveLetter -or $hasDriveLetter -or
                $hidden.AccessPaths -contains "$($state.Root)\") {
                Write-Host "WARNING: Recovery partition cleanup reported unexpected properties; continuing deployment. GptType=$($hidden.GptType); NoDefaultDriveLetter=$($hidden.NoDefaultDriveLetter); HasDriveLetter=$hasDriveLetter; AccessPaths=$($hidden.AccessPaths -join ', ')"
            }
        } catch { Write-Host "WARNING: Recovery partition mount cleanup failed; continuing deployment: $_" }
        # Only remove the unique directory created on this verified Windows volume.
        $expectedParent = [IO.Path]::GetFullPath("$windowsRoot\")
        if ([IO.Path]::GetDirectoryName($state.Backup).TrimEnd('\') + '\' -ne $expectedParent -or
            [IO.Path]::GetFileName($state.Backup) -notmatch '^CloneDeploy-Recovery-[0-9a-f]{32}$') { throw 'Invalid recovery backup cleanup path.' }
        Remove-Item -LiteralPath $state.Backup -Recurse -Force
    }
    if ($request.Operation -eq 'PrepareWim') {
        if ($request.WindowsLetter -ne 'C') { throw 'WIM deployment must use C: to preserve Windows junction targets.' }
        foreach ($letter in @($request.WindowsLetter, $request.SystemLetter, $request.RecoveryLetter)) {
            if ((Get-DeploymentDriveLetters) -contains "${letter}:\" -or
                (Get-Volume -DriveLetter $letter -ErrorAction SilentlyContinue)) {
                # Cleaning the selected disk frees its old C: mount. Never take C:
                # from another disk, removable media, or a network mapping.
                if ($letter -ne $request.WindowsLetter) { throw 'A temporary drive letter is already occupied.' }
                $null = Get-DeploymentPartition -letter $letter
            }
        }
        $efiPartitionMiB = 300
        $microsoftReservedPartitionMiB = 16
        $biosSystemPartitionMiB = 500
        $recoveryPartitionMiB = 2048
        $commands = @("select disk $($disk.Number)", 'clean')
        if ($request.Uefi) {
            $commands += @('convert gpt',"create partition efi size=$efiPartitionMiB",'format quick fs=fat32 label="System"',"assign letter=$($request.SystemLetter)","create partition msr size=$microsoftReservedPartitionMiB")
        } else {
            $commands += @('convert mbr',"create partition primary size=$biosSystemPartitionMiB",'format quick fs=ntfs label="System"',"assign letter=$($request.SystemLetter)",'active')
        }
        $commands += @('create partition primary',"shrink minimum=$recoveryPartitionMiB",'format quick fs=ntfs label="Windows"',"assign letter=$($request.WindowsLetter)",'create partition primary','format quick fs=ntfs label="Recovery"',"assign letter=$($request.RecoveryLetter)")
        Invoke-DiskPart $commands
        foreach ($letter in @($request.WindowsLetter, $request.SystemLetter, $request.RecoveryLetter)) {
            $expectedFileSystem = if ($letter -eq $request.SystemLetter -and $request.Uefi) { 'FAT32' } else { 'NTFS' }
            $null = Get-DeploymentPartition -letter $letter -fileSystem $expectedFileSystem
        }
    } elseif ($request.Operation -in @('FinishWim','FinishFfu')) {
        $ffuRecovery = $null
        if ($request.Operation -eq 'FinishFfu') {
            if (-not $request.Uefi -or (Get-Disk -Number $disk.Number).PartitionStyle -ne 'GPT') { throw 'FFU deployment requires a GPT image and UEFI boot.' }
            Update-HostStorageCache
            $partitions = @(Get-Partition -DiskNumber $disk.Number)
            $esp = @($partitions | Where-Object { $_.GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' })
            if ($esp.Count -ne 1) { throw 'FFU must contain exactly one EFI system partition.' }
            $temporary = @()
            $candidates = @()
            foreach ($partition in $partitions | Where-Object { $_.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' }) {
                if ($partition.DriveLetter) { $root = "$($partition.DriveLetter):" }
                else {
                    if (Get-Volume -DriveLetter $request.WindowsLetter -ErrorAction SilentlyContinue) { throw 'Windows drive letter is occupied.' }
                    Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber -AccessPath "$windows\"
                    $root = $windows
                }
                if (Test-Path -LiteralPath "$root\Windows\System32\config\SYSTEM") { $candidates += $partition.PartitionNumber }
                if (-not $partition.DriveLetter) { Remove-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber -AccessPath "$windows\" }
            }
            if ($candidates.Count -ne 1) { throw 'FFU must contain exactly one Windows installation.' }
            $os = Get-Partition -DiskNumber $disk.Number -PartitionNumber $candidates[0]
            if ($os.DriveLetter) { $windows = "$($os.DriveLetter):" }
            else { Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $os.PartitionNumber -AccessPath "$windows\" }
            if ($esp[0].DriveLetter) { $system = "$($esp[0].DriveLetter):" }
            else { Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $esp[0].PartitionNumber -AccessPath "$system\" }
        }
        $osPartition = Get-DeploymentPartition -letter $windows[0]
        $systemPartition = Get-DeploymentPartition -letter $system[0]
        if ($osPartition.DiskNumber -ne $disk.Number -or $systemPartition.DiskNumber -ne $disk.Number -or -not (Test-Path -LiteralPath "$windows\Windows\System32\config\SYSTEM")) { throw 'Applied Windows or system partition is invalid.' }
        if ($request.Operation -eq 'FinishFfu') {
            $osPartition = Get-DeploymentPartition -letter $windows[0] -fileSystem 'NTFS'
            $ffuRecovery = Expand-FfuWindows -osPartition $osPartition -windowsRoot $windows -recoveryRoot $recovery
        }
        $firmware = if ($request.Uefi) { 'UEFI' } else { 'BIOS' }
        $bootFile = if ($request.Uefi) { "$system\EFI\Microsoft\Boot\BCD" } else { "$system\Boot\BCD" }
        if ($request.Operation -eq 'FinishFfu') {
            # The verified target system partition contains the captured BCD. Start
            # fresh, as WIM deployment does, instead of merging stale Windows loaders.
            Write-Output "Rebuilding captured boot configuration at $bootFile"
            foreach ($suffix in @('', '.LOG', '.LOG1', '.LOG2')) {
                $capturedBootFile = "$bootFile$suffix"
                if (Test-Path -LiteralPath $capturedBootFile) {
                    Remove-Item -LiteralPath $capturedBootFile -Force -ErrorAction Stop
                }
            }
        }
        Write-Output "Configuring $firmware boot files from $windows\Windows on $system"
        & "$env:SystemRoot\System32\bcdboot.exe" "$windows\Windows" /s $system /f $firmware
        if ($LASTEXITCODE -ne 0) { throw 'BCDBoot failed.' }
        if (-not $request.Uefi) {
            & "$env:SystemRoot\System32\bootsect.exe" /nt60 $system /mbr
            if ($LASTEXITCODE -ne 0) { throw 'BIOS boot code installation failed.' }
        }
        if (-not (Test-Path -LiteralPath $bootFile)) { throw 'Boot configuration is missing after BCDBoot.' }
        $bootManager = if ($request.Uefi) { "$system\EFI\Microsoft\Boot\bootmgfw.efi" } else { "$system\bootmgr" }
        $osLoader = if ($request.Uefi) { "$windows\Windows\System32\winload.efi" } else { "$windows\Windows\System32\winload.exe" }
        foreach ($path in @($bootManager, $osLoader)) {
            if (-not [IO.File]::Exists($path)) { throw "Required boot file is missing: $path" }
        }
        if ($request.Uefi -and -not [IO.File]::Exists("$system\EFI\Boot\bootx64.efi")) {
            throw 'UEFI fallback bootloader is missing after BCDBoot /s.'
        }
        & "$env:SystemRoot\System32\bcdedit.exe" /store $bootFile /enum '{default}'
        if ($LASTEXITCODE -ne 0) { throw 'The deployed BCD store has no readable default Windows loader entry.' }
        if ($request.Uefi) { Register-DeploymentFirmwareBoot -systemRoot $system }
        if ($request.Operation -eq 'FinishWim') { Register-WimRecovery -windowsRoot $windows -recoveryRoot $recovery -bootFile $bootFile -uefi $request.Uefi }
        if ($ffuRecovery) { Register-FfuRecovery -state $ffuRecovery -windowsRoot $windows -bootFile $bootFile }
        [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($RequestFile)) 'windows-root.txt'), "$windows\")
    } else { throw 'Unsupported disk operation.' }
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
