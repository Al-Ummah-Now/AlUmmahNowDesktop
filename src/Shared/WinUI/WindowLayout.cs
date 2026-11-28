using System.Runtime.InteropServices;

namespace AlUmmahNow.Windowing;

internal static class WindowLayout
{
    private const int ExtendedStyle = -20;
    private const int LayoutRtl = 0x00400000;
    private const int NonClientRtlLayout = 6;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;

    /// <summary>Mirrors the window's native caption, including the system buttons, for an RTL app language.</summary>
    public static void Apply(nint hwnd, bool rightToLeft)
    {
        var style = GetWindowLongPtr(hwnd, ExtendedStyle);
        var updated = rightToLeft ? style | LayoutRtl : style & ~LayoutRtl;
        if (updated != style) SetWindowLongPtr(hwnd, ExtendedStyle, updated);

        var mirrored = rightToLeft ? 1 : 0;
        DwmSetWindowAttribute(hwnd, NonClientRtlLayout, in mirrored, sizeof(int));
        if (updated != style)
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, NoSize | NoMove | NoZOrder | NoActivate | FrameChanged);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
