$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$bootstrapRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
function Assert-NoLinks([string]$path) {
    $item = $path
    while ($item) {
        if (Test-Path -LiteralPath $item) {
            if ((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bootstrap paths cannot contain links.' }
        }
        $item = [IO.Path]::GetDirectoryName($item)
    }
}

function Remove-WorkingPath([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($bootstrapRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid bootstrap cleanup path.' }
    Assert-NoLinks $full
    if (Test-Path -LiteralPath $full) {
        # Check each level before descending; never follow a directory junction during cleanup.
        if ([IO.Directory]::Exists($full)) {
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($full)) { Remove-WorkingPath $child }
            [IO.Directory]::Delete($full, $false)
        } else { [IO.File]::Delete($full) }
    }
}

function Assert-ManifestSignature([string]$signature, [string]$nonce, [Uri]$uri, [string]$path) {
    if (-not $signature -or $signature.Length -gt 32768) { throw 'Missing or oversized main-server manifest signature. Upgrade main and rebuild WinPE.' }
    $parts = $signature.Split('.')
    if ($parts.Length -ne 3 -or $parts[0].Length -gt 8192) { throw 'Invalid manifest signature.' }
    $header = [Text.Encoding]::UTF8.GetString([ImagerBootstrapSignature]::Decode($parts[0])) | ConvertFrom-Json
    if (@($header.PSObject.Properties).Count -ne 3 -or $header.alg -cne 'ES256' -or $header.typ -cne 'clonedeploy-server-v1' -or @($header.x5c).Count -ne 1) { throw 'Unsupported manifest signature.' }
    [ImagerBootstrapSignature]::Verify([Convert]::FromBase64String($header.x5c[0]), [IO.File]::ReadAllBytes($caPath),
        ($parts[0] + '.' + $parts[1]), [ImagerBootstrapSignature]::Decode($parts[2]))
    $message = [Text.Encoding]::UTF8.GetString([ImagerBootstrapSignature]::Decode($parts[1])) | ConvertFrom-Json
    # Match ServerSignatures: two-minute maximum lifetime and 30 seconds of future clock skew.
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    if ($message.Issued -gt ($now + 30) -or $message.Expires -lt $now -or
        $message.Expires -lt $message.Issued -or ($message.Expires - $message.Issued) -gt 120) {
        throw 'Imager manifest has expired or its timestamps are invalid. Check the WinPE and server clocks, then retry.'
    }
    if ($message.Context -cne [ImagerBootstrapSignature]::Context($nonce, $uri.PathAndQuery) -or
        $message.Value.Status -ne 200 -or
        $message.Value.BodyHash -cne [ImagerBootstrapSignature]::Hash([IO.File]::ReadAllBytes($path))) { throw 'Modified Imager manifest or response does not match this request.' }
}

function Wait-ImagerNetwork {
    Write-Host 'Waiting up to 120 seconds for a usable network address...'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($adapter.OperationalStatus -ne [Net.NetworkInformation.OperationalStatus]::Up -or
                $adapter.NetworkInterfaceType -eq [Net.NetworkInformation.NetworkInterfaceType]::Loopback -or
                $adapter.NetworkInterfaceType -eq [Net.NetworkInformation.NetworkInterfaceType]::Tunnel) { continue }
            foreach ($unicast in $adapter.GetIPProperties().UnicastAddresses) {
                $address = $unicast.Address
                if ([Net.IPAddress]::IsLoopback($address)) { continue }
                if ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
                    $bytes = $address.GetAddressBytes()
                    if ($bytes[0] -eq 0 -or ($bytes[0] -eq 169 -and $bytes[1] -eq 254)) { continue }
                } elseif ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6) {
                    if ($address.IsIPv6LinkLocal -or $address.IsIPv6Multicast -or $address.Equals([Net.IPAddress]::IPv6Any)) { continue }
                } else { continue }
                Write-Host ('Network address ready: ' + $address)
                return
            }
        }
        if ($timer.Elapsed.TotalSeconds -ge 120) { break }
        Start-Sleep -Seconds 2
    } while ($true)
    throw 'No usable network address was available after 120 seconds. Check the network cable, switch port, DHCP, and network drivers, then retry.'
}

function Sync-ImagerTime($settings) {
    $timeServers = @('time.windows.com')
    if ($settings.PSObject.Properties['TimeServers']) { $timeServers = @($settings.TimeServers) }
    if ($timeServers.Count -gt 4) { throw 'Configure no more than four WinPE time servers.' }
    Write-Host ('Current WinPE clock: ' + [DateTime]::UtcNow.ToString('u'))
    foreach ($timeServer in $timeServers) {
        if ([string]::IsNullOrWhiteSpace([string]$timeServer) -or [Uri]::CheckHostName([string]$timeServer) -eq [UriHostNameType]::Unknown) {
            throw 'WinPE time servers must be host names or IP addresses, without a URL or port.'
        }
    }
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        foreach ($timeServer in $timeServers) {
            Write-Host ('Synchronizing time with ' + $timeServer + ' (attempt ' + $attempt + '/2)...')
            try {
                $utc = [ImagerTimeSync]::Query([string]$timeServer)
                Set-Date -Date $utc.ToLocalTime() -ErrorAction Stop | Out-Null
                Write-Host ('WinPE clock synchronized: ' + [DateTime]::UtcNow.ToString('u'))
                return
            } catch {
                Write-Warning ('Time sync failed with ' + $timeServer + ': ' + $_.Exception.GetBaseException().Message)
            }
        }
        if ($attempt -lt 2 -and $timeServers.Count -gt 0) {
            Write-Host 'Waiting 5 seconds before retrying time synchronization...'
            Start-Sleep -Seconds 5
        }
    }
    Write-Warning 'Time was not synchronized. Trying the download with the current clock and normal certificate validation.'
}

function Download-File([Uri]$uri, [string]$path, [long]$limit, [switch]$SignedManifest) {
    $request = [Net.HttpWebRequest]::Create($uri)
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.UseDefaultCredentials = $false
    if ($SignedManifest) {
        $nonceBytes = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($nonceBytes) } finally { $rng.Dispose() }
        $nonce = [BitConverter]::ToString($nonceBytes).Replace('-', '')
        $request.Headers['X-CloneDeploy-Signing-Nonce'] = $nonce
        $request.Headers['X-CloneDeploy-Imager-Signing'] = 'bootstrap-1'
    }
    $response = $request.GetResponse()
    try {
        if ([int]$response.StatusCode -ne 200 -or $response.ContentLength -gt $limit) { throw 'Unexpected Imager download response.' }
        $inputStream = $response.GetResponseStream()
        $outputStream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $buffer = New-Object byte[] 81920
            $total = 0L
            while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $total += $count
                if ($total -gt $limit) { throw 'Imager download exceeds its expected size.' }
                $outputStream.Write($buffer, 0, $count)
            }
        } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        if ($SignedManifest) { Assert-ManifestSignature $response.Headers['X-CloneDeploy-Server-Signature'] $nonce $uri $path }
    } finally { $response.Dispose() }
}

Assert-NoLinks $bootstrapRoot
$ownership = [IO.File]::Open((Join-Path $bootstrapRoot 'bootstrap.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $caPath = Join-Path $bootstrapRoot 'CloneDeploy-Root-CA.cer'
    if (-not (Test-Path -LiteralPath $caPath)) { throw 'Missing installation CA. Rebuild trusted WinPE media.' }
    $verifierPath = Join-Path $bootstrapRoot 'ImagerBootstrapSignature.cs'
    Assert-NoLinks $verifierPath
    Add-Type -Path $verifierPath
    $timeSyncPath = Join-Path $bootstrapRoot 'ImagerTimeSync.cs'
    Assert-NoLinks $timeSyncPath
    Add-Type -Path $timeSyncPath
    if (Test-Path -LiteralPath $caPath) {
        Assert-NoLinks $caPath
        $ca = [Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($caPath))
        $trustStore = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
        try {
            $trustStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $trustStore.Add($ca)
        } finally { $trustStore.Dispose(); $ca.Dispose() }
    }
    $settings = Get-Content -LiteralPath (Join-Path $bootstrapRoot 'bootstrap.json') -Raw | ConvertFrom-Json
    $architecture = if ($settings.PSObject.Properties['Architecture']) { [string]$settings.Architecture } else { 'x64' }
    if ($architecture -notin @('x64', 'arm64')) { throw 'Unsupported Imager architecture.' }
    $expectedProcessor = if ($architecture -eq 'arm64') { 'ARM64' } else { 'AMD64' }
    if ($env:PROCESSOR_ARCHITECTURE -ne $expectedProcessor) { throw 'The Imager bootstrap does not match this WinPE architecture.' }
    $server = [Uri]([string]$settings.ServerUrl)
    if (-not $server.IsAbsoluteUri -or $server.Scheme -ne 'https' -or $server.UserInfo -or $server.Query -or $server.Fragment) { throw 'Configure a valid HTTPS CloneDeploy server URL.' }
    $server = [Uri]($server.AbsoluteUri.TrimEnd('/') + '/')
    while ($true) {
        $id = [Guid]::NewGuid().ToString('N')
        $manifestPath = Join-Path $bootstrapRoot ('manifest-' + $id + '.json')
        $archivePath = Join-Path $bootstrapRoot ('download-' + $id + '.zip')
        $stage = Join-Path $bootstrapRoot ('stage-' + $id)
        try {
            Wait-ImagerNetwork
            Sync-ImagerTime $settings
            Write-Host 'Downloading the latest CloneDeploy Imager...'
            Download-File ([Uri]::new($server, ('api/imaging/client/win-' + $architecture + '.json'))) $manifestPath 65536 -SignedManifest
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ($manifest.architecture -ne $architecture) { throw 'Imager manifest architecture mismatch.' }
            if ([string]$manifest.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$manifest.sizeBytes -le 0 -or [long]$manifest.sizeBytes -gt 268435456) { throw 'Invalid Imager manifest.' }
            Download-File ([Uri]::new($server, ('api/imaging/client/win-' + $architecture + '.zip?sha256=' + $manifest.sha256))) $archivePath ([long]$manifest.sizeBytes)
            $hasher = [Security.Cryptography.SHA256]::Create()
            $hashStream = [IO.File]::OpenRead($archivePath)
            try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($hashStream)).Replace('-', '') }
            finally { $hashStream.Dispose(); $hasher.Dispose() }
            if ((Get-Item -LiteralPath $archivePath).Length -ne [long]$manifest.sizeBytes -or $actualHash -ne $manifest.sha256) { throw 'Imager download verification failed. Retry to fetch the current version.' }
            $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
            try {
                if ($zip.Entries.Count -gt 10000) { throw 'Too many files in Imager bundle.' }
                $expanded = 0L
                $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
                foreach ($entry in $zip.Entries) {
                    $name = $entry.FullName.Replace('/', '\')
                    if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or $name -match '[\x00-\x1F]' -or
                        @($name.Split('\') | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith(' ') -or $_.EndsWith('.') }).Count -gt 0 -or
                        -not $names.Add($name)) { throw 'Unsafe path in Imager bundle.' }
                    if ($name -ieq 'imager.json') { throw 'A standard Imager bundle must not contain configuration.' }
                    $expanded += $entry.Length
                    if ($expanded -gt 419430400) { throw 'Imager bundle is too large for WinPE.' }
                }
                $free = ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($bootstrapRoot))).AvailableFreeSpace
                if ($free -lt $expanded + 16777216) { throw 'Not enough WinPE RAM disk space to extract Imager. Reboot to clear temporary files.' }
                [IO.Directory]::CreateDirectory($stage) | Out-Null
                foreach ($entry in $zip.Entries) {
                    $name = $entry.FullName.Replace('/', '\')
                    $target = [IO.Path]::GetFullPath((Join-Path $stage $name))
                    if (-not $target.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Imager path escapes staging.' }
                    if ($name.EndsWith('\')) { [IO.Directory]::CreateDirectory($target) | Out-Null; continue }
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
                }
            } finally { $zip.Dispose() }
            foreach ($required in @('CloneDeploy-Imager.exe', 'CloneDeploy-Imager.dll', 'coreclr.dll', 'CloneDeploy-Imager.deps.json', 'CloneDeploy-Imager.runtimeconfig.json')) {
                if (-not [IO.File]::Exists((Join-Path $stage $required))) { throw 'Incomplete Imager bundle.' }
            }
            Remove-WorkingPath $archivePath
            $installed = Join-Path $bootstrapRoot 'Imager'
            Remove-WorkingPath $installed
            [IO.Directory]::Move($stage, $installed)
            Write-Host ('Starting CloneDeploy Imager ' + $manifest.version)
            $launchArguments = @('--config', (Join-Path $bootstrapRoot 'imager.json'))
            & (Join-Path $installed 'CloneDeploy-Imager.exe') @launchArguments
            $imagerExit = $LASTEXITCODE
            if ($imagerExit -ne 0) {
                $hexExit = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$imagerExit), 0).ToString('X8')
                Write-Host ('Imager exited with code ' + $imagerExit + ' (0x' + $hexExit + ').') -ForegroundColor Red
                Write-Host 'Preserve the Imager logs and %SystemRoot%\Logs\DISM\dism.log before rebooting.'
                Write-Host 'Press Enter to return to the command prompt'
                Read-Host | Out-Null
            }
            exit $imagerExit
        } catch {
            Write-Host ('Imager startup failed: ' + $_.Exception.Message) -ForegroundColor Red
            Write-Host ('Current WinPE clock: ' + [DateTime]::UtcNow.ToString('u'))
            Write-Host 'Check network access, the server URL, certificate trust, and the clock. Correct an inaccurate clock with Set-Date or fix NTP access. No downloaded program was started unless verification succeeded.'
            if ((Read-Host 'Enter R to retry, or press Enter to return to the command prompt') -ine 'R') { exit 1 }
        } finally {
            foreach ($temporary in @($manifestPath, $archivePath, $stage)) { Remove-WorkingPath $temporary }
        }
    }
} finally {
    $ownership.Dispose()
}
