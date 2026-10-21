# Builds the README's top image, docs\images\hero.png, from the rendered screenshots: the flyout in
# dark and light and the Settings window side by side, centred on a 16:9 canvas. The background is
# transparent, so it sits on GitHub's light and dark themes alike. Run by screenshots.ps1, or alone:
#   .\tools\hero.ps1
param([string]$Images = (Join-Path $PSScriptRoot '..\docs\images'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$names = 'flyout-dark', 'flyout-light', 'settings-expert-battery-light'
$gap = 64     # between screenshots
$pad = 96     # at least this much around them
$radius = 12  # Windows 11's rounded corners at 150%

# A rectangle with rounded corners.
function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

$shots = @(foreach ($name in $names) { [System.Drawing.Image]::FromFile((Resolve-Path (Join-Path $Images "$name.png")).Path) })
try {
    $contentWidth = ($shots | Measure-Object -Property Width -Sum).Sum + $gap * ($shots.Count - 1)
    $contentHeight = ($shots | Measure-Object -Property Height -Maximum).Maximum

    # 16:9, wide enough for the screenshots and tall enough for the tallest, with the padding round them.
    $width = $contentWidth + 2 * $pad
    $height = [int][Math]::Ceiling($width * 9 / 16)
    if ($height -lt $contentHeight + 2 * $pad) {
        $height = $contentHeight + 2 * $pad
        $width = [int][Math]::Ceiling($height * 16 / 9)
    }

    $canvas = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $x = [int](($width - $contentWidth) / 2)
        foreach ($shot in $shots) {
            $y = [int](($height - $shot.Height) / 2)

            # A soft shadow below: faint rounded rectangles, each a little larger (shows on a light page).
            $shade = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(3, 0, 0, 0))
            for ($i = 24; $i -ge 1; $i--) {
                $shadow = RoundedRect ($x - $i) ($y - $i + 10) ($shot.Width + 2 * $i) ($shot.Height + 2 * $i) ($radius + $i)
                $g.FillPath($shade, $shadow)
                $shadow.Dispose()
            }
            $shade.Dispose()

            # The screenshot with rounded corners and a faint edge (shows on a dark page).
            $frame = RoundedRect $x $y $shot.Width $shot.Height $radius
            $texture = New-Object System.Drawing.TextureBrush($shot)
            $texture.TranslateTransform($x, $y)
            $g.FillPath($texture, $frame)
            $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 128, 128, 128), 1)
            $g.DrawPath($edge, $frame)
            $edge.Dispose()
            $texture.Dispose()
            $frame.Dispose()

            $x += $shot.Width + $gap
        }
    }
    finally {
        $g.Dispose()
    }

    $out = Join-Path (Resolve-Path $Images).Path 'hero.png'
    $canvas.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
    "Hero image written to $out ($width x $height)"
}
finally {
    foreach ($shot in $shots) { $shot.Dispose() }
}
