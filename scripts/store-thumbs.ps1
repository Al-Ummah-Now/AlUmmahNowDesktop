# Draws the fixture thumbnails (thumb-1.png … thumb-8.png, 16:9, no text) that the viewer's
# AUN_DEMO catalog shows in store screenshots. Usage: store-thumbs.ps1 <folder>
param([string]$Out)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null
# Pairs from the brand palette (deep teal, lantern gold, sand, night blue).
$pairs = @(
  @("#093041", "#1F6F78"), @("#3A2A12", "#C99A3B"), @("#0E2433", "#3E7CB1"), @("#2B3A2F", "#7FA36B"),
  @("#41210F", "#D27C3F"), @("#1C1B33", "#6A5ACD"), @("#123A3A", "#E2C275"), @("#2E1A2B", "#A0526D")
)
for ($i = 0; $i -lt $pairs.Count; $i++) {
  $bmp = New-Object System.Drawing.Bitmap 640, 360
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $rect = New-Object System.Drawing.Rectangle 0, 0, 640, 360
  $a = [System.Drawing.ColorTranslator]::FromHtml($pairs[$i][0]); $b = [System.Drawing.ColorTranslator]::FromHtml($pairs[$i][1])
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $a, $b, (25 + 40 * $i)
  $g.FillRectangle($brush, $rect)
  # A soft arch, so the cards read as video frames rather than flat swatches.
  $glow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(46, 255, 255, 255))
  $g.FillEllipse($glow, 200 + 20 * ($i % 3), 60, 240, 300)
  $g.FillRectangle($glow, 200 + 20 * ($i % 3), 210, 240, 150)
  $bmp.Save((Join-Path $Out "thumb-$($i + 1).png"), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
Get-ChildItem $Out | Select-Object Name, Length
