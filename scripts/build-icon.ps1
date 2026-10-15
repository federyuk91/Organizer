param([string]$SourcePath, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$SourcePath) { $SourcePath = Join-Path $root 'assets/FileOrganizer.png' }
if (!$OutputPath) { $OutputPath = Join-Path $root 'assets/FileOrganizer.ico' }
Add-Type -AssemblyName System.Drawing
$source = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $SourcePath).Path)
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
try {
    if ($source.Width -ne $source.Height) { throw 'The icon source must be square to preserve its proportions.' }
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, [Drawing.Rectangle]::new(0, 0, $size, $size))
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        }
        finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $output = [IO.File]::Create([IO.Path]::GetFullPath($OutputPath))
    $writer = [IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        [uint32]$offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            [byte]$dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write($dimension); $writer.Write($dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length); $writer.Write($offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    }
    finally { $writer.Dispose(); $output.Dispose() }
}
finally { $source.Dispose() }
Write-Output "Created $OutputPath ($($sizes -join ', ') pixels, 32-bit alpha)."
