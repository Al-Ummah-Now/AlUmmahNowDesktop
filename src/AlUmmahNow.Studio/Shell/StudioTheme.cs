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

    private static string Text(string name)
    {
        using var reader = new StreamReader(Open(name));
        return reader.ReadToEnd();
    }

    private static Stream Open(string name) => Self.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
}
