$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$started = [DateTime]::UtcNow.AddSeconds(-5)
$watch = [Diagnostics.Stopwatch]::StartNew()
$diagnostics = Join-Path $env:ProgramData 'CloneDeploy\AgentLogs'
$finalizer = Join-Path $diagnostics 'image-prep-finalization.log'
$panther = Join-Path $env:WINDIR 'System32\Sysprep\Panther'
$window = New-Object Windows.Window
$window.Title = 'CloneDeploy image preparation progress'
$window.Width = 850
$window.Height = 560
$window.WindowStartupLocation = 'CenterScreen'
$panel = New-Object Windows.Controls.DockPanel
$panel.Margin = '16'
$header = New-Object Windows.Controls.TextBlock
$header.TextWrapping = 'Wrap'
$header.Margin = '0,0,0,12'
[Windows.Controls.DockPanel]::SetDock($header, 'Top')
[void]$panel.Children.Add($header)
$footer = New-Object Windows.Controls.TextBlock
$footer.Text = "This window remains open when the agent disconnects. Windows will restart or shut down automatically after successful preparation. Closing this viewer does not cancel preparation.`nLogs: $finalizer and $panther"
$footer.TextWrapping = 'Wrap'
$footer.Margin = '0,12,0,0'
[Windows.Controls.DockPanel]::SetDock($footer, 'Bottom')
[void]$panel.Children.Add($footer)
$text = New-Object Windows.Controls.TextBox
$text.IsReadOnly = $true
$text.TextWrapping = 'Wrap'
$text.VerticalScrollBarVisibility = 'Auto'
$text.FontFamily = 'Consolas'
[void]$panel.Children.Add($text)
$window.Content = $panel
$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(2)
$script:sysprepSeen = $false
function Read-Tail([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        # Bounded tail, with BOM detection for Windows UTF-16 Sysprep logs.
        $first = $stream.ReadByte(); $second = $stream.ReadByte()
        $encoding = if ($first -eq 255 -and $second -eq 254) { [Text.Encoding]::Unicode } else { [Text.Encoding]::UTF8 }
        $offset = [Math]::Max(0, $stream.Length - 32768)
        if ($encoding -eq [Text.Encoding]::Unicode) { $offset -= $offset % 2 }
        $stream.Position = $offset
        $reader = New-Object IO.StreamReader($stream, $encoding, $true)
        try { return $reader.ReadToEnd().TrimStart([char]0xFEFF) } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
$timer.Add_Tick({
    try {
        $fresh = @()
        $include = $false
        foreach ($line in ((Read-Tail $finalizer) -split "`r?`n")) {
            $stamp = [DateTimeOffset]::MinValue
            if ($line.Length -ge 28 -and [DateTimeOffset]::TryParse(($line -split ' ', 2)[0], [ref]$stamp)) {
                $include = $stamp.UtcDateTime -ge $started
            }
            if ($include) { $fresh += $line }
        }
        $activity = $fresh -join "`r`n"
        if ($activity -match 'Starting Sysprep as the final operation') { $script:sysprepSeen = $true }
        $status = if ($activity -match '(?i)failed:|failed with|Do not capture') { 'Preparation failed. Review the details below before retrying.' }
            elseif ($script:sysprepSeen -and (Get-Process sysprep -ErrorAction SilentlyContinue)) { 'Sysprep is running. Waiting for Windows to restart or shut down.' }
            elseif ($script:sysprepSeen) { 'Sysprep was started. Waiting for shutdown or a final result; completion is not yet confirmed.' }
            elseif ($fresh.Count) { 'Image preparation is in progress.' }
            else { 'Waiting for preparation activity. If startup failed in the portal, close this viewer.' }
        $header.Text = "$status`nElapsed: $($watch.Elapsed.ToString('hh\:mm\:ss'))"
        if ($script:sysprepSeen) {
            foreach ($name in @('setupact.log', 'setuperr.log')) {
                $path = Join-Path $panther $name
                if ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).LastWriteTimeUtc -ge $started) {
                    $activity += "`r`n--- Sysprep $name (recent tail) ---`r`n" + (Read-Tail $path)
                }
            }
        }
        if ($text.Text -ne $activity) { $text.Text = $activity; $text.ScrollToEnd() }
    } catch { $header.Text = "Progress is temporarily unavailable: $($_.Exception.Message). Preparation continues independently." }
})
$window.Add_Closed({ $timer.Stop() })
$header.Text = 'Waiting for image preparation to start...'
$timer.Start()
[void]$window.ShowDialog()
