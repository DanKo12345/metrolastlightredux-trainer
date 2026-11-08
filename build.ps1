# Builds the trainer and the setup program with the C# compiler that ships with Windows.
# Output: dist\MetroLastLightReduxTrainer.exe and dist\MetroLastLightReduxTrainerSetup.exe
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$csc = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
New-Item -ItemType Directory -Force $dist, (Join-Path $root 'assets') | Out-Null

function New-IconImage([int]$size, [Drawing.Color]$accent) {
    $bitmap = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([Drawing.Color]::FromArgb(17, 19, 23))
    $g.DrawEllipse((New-Object Drawing.Pen $accent, ($size * 0.07)), $size * 0.09, $size * 0.09, $size * 0.82, $size * 0.82)
    $font = New-Object Drawing.Font 'Segoe UI Semibold', ($size * 0.42), ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
    $format = New-Object Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'
    $g.DrawString('M', $font, (New-Object Drawing.SolidBrush $accent), (New-Object Drawing.RectangleF 0, 0, $size, $size), $format)
    $g.Dispose()
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return ,$stream.ToArray()
}

function New-Icon([string]$icon, [Drawing.Color]$accent) {
    if (Test-Path $icon) { return }
    $sizes = 16, 32, 48, 256
    $images = @($sizes | ForEach-Object { ,(New-IconImage $_ $accent) })
    $file = [IO.File]::Create($icon)
    $writer = New-Object IO.BinaryWriter $file
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $side = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$side); $writer.Write([byte]$side); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
    $writer.Close()
}

function Invoke-Csc([string]$output, [string]$icon, [string[]]$sources, [string[]]$extra) {
    $arguments = @('/nologo', '/utf8output', '/codepage:65001', '/target:winexe', '/platform:x64', '/optimize+',
        '/warnaserror-', "/win32icon:$icon", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll',
        '/r:Microsoft.CSharp.dll', "/out:$output") + $extra + $sources
    $lines = & $csc @arguments | Where-Object { $_ -notmatch 'C# 5|language versions|fwlink' -and $_ -ne '' }
    if ($LASTEXITCODE -ne 0) { $lines; throw "csc failed for $output" }
    $lines
}

$src = Join-Path $root 'src'
$shared = (Join-Path $src 'Product.cs'), (Join-Path $src 'Localization.cs'), (Join-Path $src 'Ui.cs')
# The icon colour matches Theme.Accent in Ui.cs.
$icon = Join-Path $root 'assets\icon_lastlight.ico'
New-Icon $icon ([Drawing.Color]::FromArgb(232, 108, 52))
$trainer = Join-Path $dist 'MetroLastLightReduxTrainer.exe'
$setup = Join-Path $dist 'MetroLastLightReduxTrainerSetup.exe'
Invoke-Csc $trainer $icon ($shared + (Join-Path $src 'Trainer.cs')) @()
Invoke-Csc $setup $icon ($shared + (Join-Path $src 'Setup.cs')) @("/resource:$trainer,trainer.exe")
Get-Item $trainer, $setup | Select-Object Name, Length
