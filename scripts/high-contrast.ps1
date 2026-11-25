# Turns Windows' high contrast on or off on a CI runner, for review frames. Usage: high-contrast.ps1 on|off|load
# "load" only compiles the type below, so that it is not compiled while an app waits. The line says how long the change took.
param([string]$State = "on")
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class AunContrast {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct HIGHCONTRAST { public uint cbSize; public uint dwFlags; public IntPtr lpszDefaultScheme; }
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern bool SystemParametersInfo(uint action, uint size, ref HIGHCONTRAST hc, uint flags);
  [DllImport("user32.dll")] static extern uint GetSysColor(int index);
  public static string Set(bool on) {
    var hc = new HIGHCONTRAST(); hc.cbSize = (uint)Marshal.SizeOf(typeof(HIGHCONTRAST));
    SystemParametersInfo(0x42, hc.cbSize, ref hc, 0);
    hc.dwFlags = on ? (hc.dwFlags | 1u) : (hc.dwFlags & ~1u);
    var scheme = Marshal.StringToHGlobalUni("High Contrast Black");
    if (on) hc.lpszDefaultScheme = scheme;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var ok = SystemParametersInfo(0x43, hc.cbSize, ref hc, 2);
    var took = clock.ElapsedMilliseconds;
    System.Threading.Thread.Sleep(8000);
    var now = new HIGHCONTRAST(); now.cbSize = hc.cbSize;
    SystemParametersInfo(0x42, now.cbSize, ref now, 0);
    return "set=" + ok + " on=" + ((now.dwFlags & 1) == 1) + " window=" + GetSysColor(5).ToString("X6") + " text=" + GetSysColor(8).ToString("X6") + " hotlight=" + GetSysColor(26).ToString("X6") + " ms=" + took;
  }
}
"@
if ($State -eq "load") { return }
"high contrast ${State}: " + [AunContrast]::Set($State -eq "on")
