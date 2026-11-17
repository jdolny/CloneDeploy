param([switch]$Mac)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
# Render this logo's paths and vertical gradient using WPF; no third-party conversion tools.
[xml]$svg = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CloneDeploy.svg') -Raw
$culture = [Globalization.CultureInfo]::InvariantCulture
$viewBox = $svg.svg.viewBox.Split(' ') | ForEach-Object { [double]::Parse($_, $culture) }
$width = $viewBox[2]; $height = $viewBox[3]
$frames = @()
$sizes = if ($Mac) { @(16, 32, 64, 128, 256, 512, 1024) } else { @(16, 24, 32, 48, 64, 128, 256) }
foreach ($size in $sizes) {
    $visual = New-Object Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    $scale = $size / $width
    $drawing.PushTransform((New-Object Windows.Media.TranslateTransform(0, (($size - $height * $scale) / 2))))
    $drawing.PushTransform((New-Object Windows.Media.ScaleTransform($scale, $scale)))
    foreach ($path in $svg.svg.path) {
        if ($path.fill -eq 'url(#b)') {
            $brush = New-Object Windows.Media.LinearGradientBrush
            $brush.MappingMode = [Windows.Media.BrushMappingMode]::Absolute
            $brush.StartPoint = New-Object Windows.Point(202.696, 0)
            $brush.EndPoint = New-Object Windows.Point(202.696, $height)
            foreach ($stop in $svg.svg.defs.linearGradient.stop) {
                $color = [Windows.Media.ColorConverter]::ConvertFromString($stop.'stop-color')
                $brush.GradientStops.Add((New-Object Windows.Media.GradientStop($color, ([double]::Parse($stop.offset, $culture)))))
            }
        } else { $brush = New-Object Windows.Media.SolidColorBrush([Windows.Media.ColorConverter]::ConvertFromString($path.fill)) }
        $drawing.DrawGeometry($brush, $null, [Windows.Media.Geometry]::Parse($path.d))
    }
    $drawing.Pop(); $drawing.Pop(); $drawing.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32))
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    $frames += ,$stream.ToArray()
    $stream.Dispose()
}
# Modern ICNS entries contain PNG data. Lengths include the eight-byte header
# and use network byte order, unlike the Windows ICO directory below.
if ($Mac) {
    $types = @('icp4', 'icp5', 'icp6', 'ic07', 'ic08', 'ic09', 'ic10')
    $output = [IO.File]::Create((Join-Path $PSScriptRoot 'CloneDeploy.icns'))
    $writer = New-Object IO.BinaryWriter($output)
    function Write-BigEndian([uint32]$Value) {
        $bytes = [BitConverter]::GetBytes($Value)
        if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
        $writer.Write([byte[]]$bytes)
    }
    try {
        $length = 8
        foreach ($frame in $frames) { $length += 8 + $frame.Length }
        $writer.Write([Text.Encoding]::ASCII.GetBytes('icns'))
        Write-BigEndian $length
        for ($i = 0; $i -lt $frames.Count; $i++) {
            $writer.Write([Text.Encoding]::ASCII.GetBytes($types[$i]))
            Write-BigEndian ($frames[$i].Length + 8)
            $writer.Write([byte[]]$frames[$i])
        }
    } finally { $writer.Dispose(); $output.Dispose() }
    return
}
$output = [IO.File]::Create((Join-Path $PSScriptRoot 'CloneDeploy.ico'))
$writer = New-Object IO.BinaryWriter($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $output.Dispose() }
