using System.Reflection;

namespace AlUmmahNow.Studio.Shell;

/// <summary>
/// Lantern Glass assets shipped inside the Studio assembly: the Fawanees tokens from the shared native
/// theme registry (WinUI dictionaries, GTK stylesheets), the Studio's own component styles built on
/// them, and the owner's lantern-string art for the night header.
/// </summary>
public static class StudioTheme
{
    private static readonly Assembly Self = typeof(StudioTheme).Assembly;

    /// <summary>"Fanous.Colors.xaml", "Fanous.Tokens.xaml", "style.css", "style-dark.css".</summary>
    public static string Fanous(string file) => AlUmmahNow.App.Theme.Fanous.File(file);

    /// <summary>The native registry's GTK stylesheet for Fawanees (colours and materials), Light or Dark.</summary>
    public static string NativeGtk(bool dark) => AlUmmahNow.App.Theme.Fanous.File(dark ? "native-fawanees-dark.css" : "native-fawanees.css");

    /// <summary>The Studio's own styles: "Glass.xaml" (WinUI), "glass.css" (GTK).</summary>
    public static string Studio(string file) => Text("studio/theme/" + file);

    /// <summary>The lantern-string band (PNG, 1920×115, transparent), cropped from the Fawanees street art.</summary>
    public static Stream LanternString() => Open("studio/lantern-string.png");

    /// <summary>The Color entries of a generated WinUI dictionary, as ARGB per theme key ("Default" = dark, "Light").</summary>
    public static Dictionary<string, Dictionary<string, uint>> ReadXamlColors(string xaml) => AlUmmahNow.App.Theme.Fanous.ReadXamlColors(xaml);

    /// <summary>The videos app's icon (the owner's lantern-window art, as is; 180×180 PNG), from the web app.</summary>
    public static Stream AppIcon() => Open("studio/app-icon.png");

    /// <summary>The gold over the surface of the card that is picked, at its top and bottom (glass.css has the same).</summary>
    public const double PickedTop = 0.16, PickedBottom = 0.04;

    /// <summary>One color over another, as one opaque color: what is behind it does not show through.</summary>
    public static uint Over(uint color, uint surface, double alpha)
    {
        uint Channel(int shift) => (uint)Math.Round((((color >> shift) & 255) * alpha) + (((surface >> shift) & 255) * (1 - alpha))) << shift;
        return 0xFF000000u | Channel(16) | Channel(8) | Channel(0);
    }

    /// <summary>The contrast of two opaque colors (WCAG); readable text has at least 4.5.</summary>
    public static double Contrast(uint a, uint b)
    {
        static double Channel(uint v) => v / 255.0 <= 0.03928 ? v / 255.0 / 12.92 : Math.Pow(((v / 255.0) + 0.055) / 1.055, 2.4);
        static double Light(uint c) => (0.2126 * Channel((c >> 16) & 255)) + (0.7152 * Channel((c >> 8) & 255)) + (0.0722 * Channel(c & 255));
        var (x, y) = (Light(a), Light(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static string Text(string name)
    {
        using var reader = new StreamReader(Open(name));
        return reader.ReadToEnd();
    }

    private static Stream Open(string name) => Self.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
}
