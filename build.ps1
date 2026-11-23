# Builds ROM-Check-O-MATIC.exe using the C# compiler built into Windows (.NET Framework 4.x).
# Run:  powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$ico  = Join-Path $src 'icon.ico'
$out  = Join-Path $root 'ROM-Check-O-MATIC.exe'
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $fw 'csc.exe'))) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc  = Join-Path $fw 'csc.exe'

# Draw the app icon (a green tick on a cartridge) if it isn't there yet. Sizes stored as PNG images inside the .ico.
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $sizes = 16, 32, 48, 256
    $images = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        # Cartridge body
        $m = $s * 0.10
        $body = New-Object System.Drawing.RectangleF $m, ($s * 0.06), ($s - 2 * $m), ($s * 0.88)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 70, 74, 84))), $body)
        $g.DrawRectangle((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 30, 32, 38)), ([Math]::Max(1, $s / 32))), $m, ($s * 0.06), ($s - 2 * $m), ($s * 0.88))
        # Label
        $g.FillRectangle([System.Drawing.Brushes]::WhiteSmoke, ($s * 0.2), ($s * 0.16), ($s * 0.6), ($s * 0.46))
        # Tick
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 30, 170, 60)), ([Math]::Max(2, $s * 0.12))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($s * 0.30), ($s * 0.40)),
            (New-Object System.Drawing.PointF ($s * 0.45), ($s * 0.53)),
            (New-Object System.Drawing.PointF ($s * 0.72), ($s * 0.25)))
        $g.DrawLines($pen, $pts)
        # Contacts
        $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 214, 170, 60))
        if ($s -ge 32) { for ($i = 0; $i -lt 5; $i++) { $g.FillRectangle($gold, ($s * (0.22 + $i * 0.12)), ($s * 0.78), ($s * 0.07), ($s * 0.12)) } }
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }
    $fs = [System.IO.File]::Create($ico)
    $w = New-Object System.IO.BinaryWriter $fs
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Close()
    Write-Host "Created $ico"
}

$sources = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /out:"$out" /win32icon:"$ico" /win32manifest:"$src\app.manifest" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:"$fw\System.IO.Compression.dll" /reference:"$fw\System.IO.Compression.FileSystem.dll" `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out"

# Package a release zip: the .exe, README and LICENSE (the .ini is created on first run).
$version = [regex]::Match((Get-Content (Join-Path $src 'Program.cs') -Raw), 'AssemblyVersion\("(\d+)\.(\d+)').Groups
$zipName = "ROM-Check-O-MATIC-v$($version[1].Value).$($version[2].Value).zip"
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist $zipName
if (Test-Path $zip) { Remove-Item $zip }
$pack = @($out) + (@('README.md', 'LICENSE') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ })
Compress-Archive -Path $pack -DestinationPath $zip
Write-Host "Packaged $zip"
