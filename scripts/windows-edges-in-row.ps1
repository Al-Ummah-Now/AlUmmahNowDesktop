# Fails unless a row of a frame has at least so many pixels that stand out (WCAG contrast) from its first pixel, as drawn:
# the edges of a chart's bars on their card. Usage: windows-edges-in-row.ps1 <frame.png> <y> <from x> <to x> <contrast> <pixels>
param([string]$Frame, [int]$Y, [int]$From, [int]$To, [double]$Least, [int]$Count)
Add-Type -AssemblyName System.Drawing
function Channel([double]$v) { $v /= 255; if ($v -le 0.03928) { $v / 12.92 } else { [Math]::Pow(($v + 0.055) / 1.055, 2.4) } }
function Light($c) { 0.2126 * (Channel $c.R) + 0.7152 * (Channel $c.G) + 0.0722 * (Channel $c.B) }
$bmp = New-Object System.Drawing.Bitmap $Frame
$card = Light $bmp.GetPixel($From, $Y)
$seen = 0
for ($x = $From; $x -lt $To; $x++) {
  $l = Light $bmp.GetPixel($x, $Y)
  if (([Math]::Max($l, $card) + 0.05) / ([Math]::Min($l, $card) + 0.05) -ge $Least) { $seen++ }
}
$bmp.Dispose()
"$(Split-Path -Leaf $Frame) row ${Y}: $seen pixels of $Least or more"
if ($seen -lt $Count) { throw "the bars' edges are not seen: $seen of $Count" }
