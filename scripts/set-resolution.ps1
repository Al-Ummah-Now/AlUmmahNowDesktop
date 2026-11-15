# Sets the primary display mode on a CI runner (default 1024x768) so store screenshots are at a
# Microsoft Store size. Usage: set-resolution.ps1 1920 1080
param([int]$Width = 1920, [int]$Height = 1080)
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class AunDisplay {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
    public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string name, int mode, ref DEVMODE dm);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
  public static int Set(int w, int h) {
    var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
    if (!EnumDisplaySettings(null, -1, ref dm)) return -100;
    dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
    return ChangeDisplaySettings(ref dm, 1);
  }
}
"@
$r = [AunDisplay]::Set($Width, $Height)
Add-Type -AssemblyName System.Windows.Forms
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
"ChangeDisplaySettings=$r screen=$($b.Width)x$($b.Height)"
if ($b.Width -lt $Width -or $b.Height -lt $Height) {
  # Older runners ship the Server cmdlet instead.
  try { Set-DisplayResolution -Width $Width -Height $Height -Force -ErrorAction Stop } catch { "Set-DisplayResolution: $_" }
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  "screen=$($b.Width)x$($b.Height)"
}
