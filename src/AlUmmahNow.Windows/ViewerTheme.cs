using AlUmmahNow.App.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AlUmmahNow.Windows;

/// <summary>
/// The Fawanees accent for the viewer, from the shared native theme registry (as in Studio): lantern
/// gold replaces the Windows accent in Light and Dark, with the registry's on-gold and link colours.
/// </summary>
internal static class ViewerTheme
{
    public static void Apply(Application app)
    {
        // Fully qualified: inside AlUmmahNow.*, "Fanous" alone names the registry namespace AlUmmahNow.Fanous.
        var themes = AlUmmahNow.App.Theme.Fanous.ReadXamlColors(AlUmmahNow.App.Theme.Fanous.File("Fanous.Colors.xaml"));
        Fill(Theme(app, "Dark"), themes["Default"]);
        Fill(Theme(app, "Light"), themes["Light"]);
    }

    private static ResourceDictionary Theme(Application app, string key)
    {
        if (app.Resources.ThemeDictionaries.TryGetValue(key, out var existing) && existing is ResourceDictionary d) return d;
        var created = new ResourceDictionary();
        app.Resources.ThemeDictionaries[key] = created;
        return created;
    }

    private static void Fill(ResourceDictionary t, Dictionary<string, uint> f)
    {
        Color C(string name)
        {
            var v = f["Fanous" + name + "Color"];
            return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        var primary = C("Primary");
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorDark1" }) t[key] = primary;
        t["SystemAccentColorLight2"] = C("PrimaryHover");
        t["SystemAccentColorLight3"] = C("PrimarySheen");
        t["SystemAccentColorDark2"] = C("PrimaryPressed");
        t["SystemAccentColorDark3"] = C("PrimaryPressed");
        t["AccentFillColorDefaultBrush"] = new SolidColorBrush(primary);
        t["AccentFillColorSecondaryBrush"] = new SolidColorBrush(C("PrimaryHover"));
        t["AccentFillColorTertiaryBrush"] = new SolidColorBrush(C("PrimaryPressed"));
        t["AccentFillColorDisabledBrush"] = new SolidColorBrush(C("PrimaryDisabled"));
        t["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(C("OnPrimary"));
        t["TextOnAccentFillColorSecondaryBrush"] = new SolidColorBrush(C("OnPrimary"));
        t["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(C("Link"));
        t["HyperlinkForeground"] = new SolidColorBrush(C("Link"));
    }
}
