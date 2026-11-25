using AlUmmahNow.App.Theme;
using AlUmmahNow.Contrast;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    /// merged after the framework's own, which answers before the application's theme dictionaries, and in high contrast too.
    /// </summary>
    public static void Apply(Application app)
    {
        var merged = new ResourceDictionary();
        merged.ThemeDictionaries["Dark"] = Accent(dark: true);
        merged.ThemeDictionaries["Light"] = Accent(dark: false);
        merged.ThemeDictionaries["HighContrast"] = SystemBrushes.Controls();
        app.Resources.MergedDictionaries.Add(merged);
    }

    /// <summary>A registry color ("Primary", "OnPrimary") of the viewer's theme, for the parts that are no control.</summary>
    public static SolidColorBrush Brush(string name, bool dark) => new(ColorOf(name, dark));

    /// <summary>The same, and the system's color in high contrast.</summary>
    public static SolidColorBrush Brush(string name, bool dark, HighContrastRole role) => SystemBrushes.Brush(ColorOf(name, dark), role);

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
            // Not gold in light mode, where it is under 3:1 on the page: the registry's accent is darker there
            t["SliderTrackValueFill" + state] = B("Accent");
            t["SliderThumbBackground" + state] = B("Accent");
            t["SliderInnerThumbBackground" + state] = B("Accent");
        }

        // The thumb's ring is the framework's, and white on the light page: its edge is what is seen
        t["SliderThumbBorderBrush"] = B("PrimaryEdge");

        t["ComboBoxItemPillFillBrush"] = B("Primary");
        return t;
    }

    /// <summary>
    /// The parts of a page with the accent, and which of them are not in the theme's color or cannot be seen: a control's
    /// parts have 3:1 to what is beside them (CI's scenarios ask). High contrast has Windows' own colors.
    /// </summary>
    public static (int Parts, string Wrong) Check(Panel root, bool dark)
    {
        var parts = 0;
        var high = SystemBrushes.IsHighContrast;
        var page = (root.Background as SolidColorBrush)?.Color ?? default;
        var wrong = new List<string>();
        void Is(string part, Brush? brush, string name)
        {
            parts++;
            if (!high && (brush is not SolidColorBrush solid || solid.Color != ColorOf(name, dark))) wrong.Add(part);
        }

        void IsSeen(string part, Brush? brush, Brush? beside)
        {
            parts++;
            var contrast = brush is SolidColorBrush a && beside is SolidColorBrush b ? ColorMath.Contrast(OnPage(a.Color, page), OnPage(b.Color, page)) : 0;
            if (!high && contrast < 3) wrong.Add(FormattableString.Invariant($"{part} {Math.Round(contrast, 2)}"));
        }

        foreach (var part in Descendants(root))
        {
            if (part is Slider { IsEnabled: true, ActualWidth: > 0 } slider)
            {
                var fill = Descendants(slider).OfType<Rectangle>().FirstOrDefault(r => r.Name == "HorizontalDecreaseRect")?.Fill;
                var rest = Descendants(slider).OfType<Rectangle>().FirstOrDefault(r => r.Name == "HorizontalTrackRect")?.Fill;
                var thumb = Descendants(slider).OfType<Ellipse>().FirstOrDefault(e => e.Name == "SliderInnerThumb")?.Fill;
                var ring = Descendants(slider).OfType<Thumb>().Where(t => t.Name == "HorizontalThumb").SelectMany(Descendants).OfType<Border>().FirstOrDefault();
                Is("seek bar", fill, "Accent");
                Is("thumb", thumb, "Accent");
                Is("thumb's edge", ring?.BorderBrush, "PrimaryEdge");
                IsSeen("seek bar", fill, root.Background);
                IsSeen("the rest of the bar", rest, root.Background);
                IsSeen("thumb", thumb, ring?.Background);
                IsSeen("thumb's edge", ring?.BorderBrush, root.Background);
            }

            if (part is Button { ActualWidth: > 0 } button && button.Style != null && button.Style == AccentButton)
            {
                Is("button", Descendants(button).OfType<ContentPresenter>().FirstOrDefault()?.Background, button.IsEnabled ? "Primary" : "PrimaryDisabled");
                Is("button's text", Descendants(button).OfType<ContentPresenter>().FirstOrDefault()?.Foreground, button.IsEnabled ? "OnPrimary" : "OnPrimaryDisabled");
            }
        }

        return (parts, string.Join(", ", wrong));
    }

    /// <summary>A color that lets the page through, as it is drawn on it.</summary>
    private static uint OnPage(Color color, Color page)
    {
        static uint Argb(Color c) => 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        return ColorMath.Over(Argb(color), Argb(page), color.A / 255.0);
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
