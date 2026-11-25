using AlUmmahNow.App.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AlUmmahNow.Windows;

/// <summary>
/// The Fawanees accent for the viewer, from the shared native theme registry (as in Studio): lantern
/// gold replaces the Windows accent in Light and Dark, with the registry's on-gold and link colors.
/// </summary>
internal static class ViewerTheme
{
    // Fully qualified: inside AlUmmahNow.*, "Fanous" alone names the registry namespace AlUmmahNow.Fanous.
    private static readonly Lazy<Dictionary<string, Dictionary<string, Color>>> Colors = new(() =>
        AlUmmahNow.App.Theme.Fanous.ReadXamlColors(AlUmmahNow.App.Theme.Fanous.File("Fanous.Colors.xaml")).ToDictionary(
            t => t.Key,
            t => t.Value.ToDictionary(c => c.Key, c => Color.FromArgb((byte)(c.Value >> 24), (byte)(c.Value >> 16), (byte)(c.Value >> 8), (byte)c.Value))));

    /// <summary>
    /// Not before OnLaunched: Application.Resources cannot be read in App's constructor. The colors are a dictionary
    /// merged after the framework's own, which answers before the application's theme dictionaries. High contrast has
    /// none here and keeps the framework's.
    /// </summary>
    public static void Apply(Application app)
    {
        var merged = new ResourceDictionary();
        merged.ThemeDictionaries["Dark"] = Accent(dark: true);
        merged.ThemeDictionaries["Light"] = Accent(dark: false);
        app.Resources.MergedDictionaries.Add(merged);
    }

    /// <summary>A registry color ("Primary", "OnPrimary") of the viewer's theme, for the parts that are no control.</summary>
    public static SolidColorBrush Brush(string name, bool dark) => new(ColorOf(name, dark));

    // The registry's dark dictionary is "Default" (the brand is dark-first).
    private static Color ColorOf(string name, bool dark) => Colors.Value[dark ? "Default" : "Light"]["Fanous" + name + "Color"];

    private static ResourceDictionary Accent(bool dark)
    {
        Color C(string name) => ColorOf(name, dark);
        SolidColorBrush B(string name) => Brush(name, dark);
        var t = new ResourceDictionary();
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorDark1" }) t[key] = C("Primary");
        t["SystemAccentColorLight2"] = C("PrimaryHover");
        t["SystemAccentColorLight3"] = C("PrimarySheen");
        t["SystemAccentColorDark2"] = C("PrimaryPressed");
        t["SystemAccentColorDark3"] = C("PrimaryPressed");
        t["AccentFillColorDefaultBrush"] = B("Primary");
        t["AccentFillColorSecondaryBrush"] = B("PrimaryHover");
        t["AccentFillColorTertiaryBrush"] = B("PrimaryPressed");
        t["AccentFillColorDisabledBrush"] = B("PrimaryDisabled");
        t["TextOnAccentFillColorPrimaryBrush"] = B("OnPrimary");
        t["TextOnAccentFillColorSecondaryBrush"] = B("OnPrimary");
        t["AccentTextFillColorPrimaryBrush"] = B("Link");
        t["HyperlinkForeground"] = B("Link");
        t["FocusStrokeColorOuterBrush"] = B("FocusRing");

        // The controls' own names: the framework has resolved them to its own brushes already.
        foreach (var (state, fill) in new[] { ("", "Primary"), ("PointerOver", "PrimaryHover"), ("Pressed", "PrimaryPressed") })
        {
            t["AccentButtonBackground" + state] = B(fill);
            t["AccentButtonForeground" + state] = B("OnPrimary");
            t["HyperlinkButtonForeground" + state] = B("Link");
            t["SliderTrackValueFill" + state] = B("Primary");
            t["SliderThumbBackground" + state] = B("Primary");
            t["SliderInnerThumbBackground" + state] = B("Primary");
        }

        t["ComboBoxItemPillFillBrush"] = B("Primary");
        return t;
    }

    /// <summary>The parts of a page with the accent, and which of them are not gold (CI's scenarios ask).</summary>
    public static (int Parts, string Wrong) Check(DependencyObject root, bool dark)
    {
        var parts = 0;
        var wrong = new List<string>();
        void Is(string part, Brush? brush, string name)
        {
            parts++;
            if (brush is not SolidColorBrush solid || solid.Color != ColorOf(name, dark)) wrong.Add(part);
        }

        foreach (var part in Descendants(root))
        {
            if (part is Slider { IsEnabled: true, ActualWidth: > 0 } slider)
            {
                Is("seek bar", Descendants(slider).OfType<Rectangle>().FirstOrDefault(r => r.Name == "HorizontalDecreaseRect")?.Fill, "Primary");
                Is("thumb", Descendants(slider).OfType<Ellipse>().FirstOrDefault(e => e.Name == "SliderInnerThumb")?.Fill, "Primary");
            }

            if (part is Button { ActualWidth: > 0 } button && button.Style != null && button.Style == AccentButton)
            {
                Is("button", Descendants(button).OfType<ContentPresenter>().FirstOrDefault()?.Background, "Primary");
                Is("button's text", Descendants(button).OfType<ContentPresenter>().FirstOrDefault()?.Foreground, "OnPrimary");
            }
        }

        return (parts, string.Join(", ", wrong));
    }

    public static Style AccentButton => (Style)Application.Current.Resources["AccentButtonStyle"];

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }
}
