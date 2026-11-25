using AlUmmahNow.App.Theme;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AlUmmahNow.Contrast;

/// <summary>
/// Brushes of both Windows apps that are a color of the theme's, and in high contrast the system's for their role, also
/// as it is turned on or off, or its colors change, while the app runs. There is one brush for a color and a role, and
/// it is painted anew: what is on screen has it already.
/// </summary>
internal static class SystemBrushes
{
    private static readonly UISettings Settings = new();
    private static readonly AccessibilitySettings Accessibility = new();
    private static readonly List<Action<bool>> Painters = new();
    private static readonly Dictionary<(Color, HighContrastRole), SolidColorBrush> Solid = new();
    private static readonly Dictionary<(Color, Color, Color, bool, HighContrastRole), LinearGradientBrush> Gradients = new();

    /// <summary>High contrast is on, and the brushes are in its colors.</summary>
    public static bool IsHighContrast { get; private set; }

    /// <summary>The brushes were painted anew.</summary>
    public static event Action? Painted;

    /// <summary>Once, on the window's thread, before any brush is drawn.</summary>
    public static void Start()
    {
        var ui = DispatcherQueue.GetForCurrentThread();
        IsHighContrast = Accessibility.HighContrast;
        Settings.ColorValuesChanged += (_, _) => ui.TryEnqueue(() =>
        {
            IsHighContrast = Accessibility.HighContrast;
            foreach (var paint in Painters) paint(IsHighContrast);
            foreach (var (part, opacity) in Dims) if (part.TryGetTarget(out var shown)) shown.Opacity = IsHighContrast ? 1 : opacity;
            Painted?.Invoke();
        });
    }

    public static Color ColorOf(HighContrastRole role) => role switch
    {
        HighContrastRole.Window => Settings.UIElementColor(UIElementType.Window),
        HighContrastRole.Link => Settings.UIElementColor(UIElementType.Hotlight),
        HighContrastRole.Highlight => Settings.UIElementColor(UIElementType.Highlight),
        HighContrastRole.HighlightText => Settings.UIElementColor(UIElementType.HighlightText),
        HighContrastRole.None => Colors.Transparent,
        _ => Settings.UIElementColor(UIElementType.WindowText),
    };

    public static SolidColorBrush Brush(Color color, HighContrastRole role)
    {
        if (Solid.TryGetValue((color, role), out var known)) return known;
        var brush = new SolidColorBrush(color);
        Solid[(color, role)] = brush;
        Follow(high => brush.Color = high ? ColorOf(role) : color);
        return brush;
    }

    /// <summary>A gradient from the top to the bottom, or from the start over the middle to the end.</summary>
    public static LinearGradientBrush Gradient(Color first, Color middle, Color last, bool down, HighContrastRole role)
    {
        if (Gradients.TryGetValue((first, middle, last, down, role), out var known)) return known;
        var brush = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, down ? 0 : 0.5),
            EndPoint = new global::Windows.Foundation.Point(down ? 0 : 1, down ? 1 : 0.5),
        };
        var colors = new[] { first, middle, last };
        for (var i = 0; i < 3; i++) brush.GradientStops.Add(new GradientStop { Color = colors[i], Offset = i / 2.0 });
        Gradients[(first, middle, last, down, role)] = brush;
        Follow(high =>
        {
            for (var i = 0; i < 3; i++) brush.GradientStops[i].Color = high ? ColorOf(role) : colors[i];
        });
        return brush;
    }

    /// <summary>
    /// The controls' own names in high contrast. A dictionary that is merged after the framework's answers for its names in
    /// high contrast too, with the theme's colors, when it has no part for it.
    /// </summary>
    public static ResourceDictionary Controls()
    {
        SolidColorBrush B(HighContrastRole role) => Brush(Colors.Transparent, role);
        var d = new ResourceDictionary();
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            d["HyperlinkButtonForeground" + state] = B(HighContrastRole.Link);
            d["AccentButtonBackground" + state] = B(HighContrastRole.Highlight);
            d["AccentButtonForeground" + state] = B(HighContrastRole.HighlightText);
            d["AccentButtonBorderBrush" + state] = B(HighContrastRole.Text);
            d["ToggleButtonBackgroundChecked" + state] = B(HighContrastRole.Highlight);
            d["ToggleButtonForegroundChecked" + state] = B(HighContrastRole.HighlightText);
            foreach (var on in new[] { "Checked", "Indeterminate" })
            {
                d["CheckBoxCheckBackgroundFill" + on + state] = B(HighContrastRole.Highlight);
                d["CheckBoxCheckGlyphForeground" + on + state] = B(HighContrastRole.HighlightText);
            }

            d["SliderTrackValueFill" + state] = B(HighContrastRole.Highlight);
            d["SliderTrackFill" + state] = B(HighContrastRole.Text);
            d["SliderThumbBackground" + state] = B(HighContrastRole.Highlight);
            d["SliderInnerThumbBackground" + state] = B(HighContrastRole.Highlight);
        }

        d["SliderThumbBorderBrush"] = B(HighContrastRole.Text);
        d["SliderOuterThumbBackground"] = B(HighContrastRole.Window);
        d["ComboBoxItemPillFillBrush"] = B(HighContrastRole.Highlight);
        d["HyperlinkForeground"] = B(HighContrastRole.Link);
        return d;
    }

    private static readonly List<(WeakReference<UIElement> Part, double Opacity)> Dims = new();

    /// <summary>
    /// A part that is less than opaque, and in high contrast wholly opaque: there a part of a color is not the system's.
    /// The parts come and go with the pages, so they are not held.
    /// </summary>
    public static T Dim<T>(this T part, double opacity)
        where T : FrameworkElement
    {
        Dims.RemoveAll(d => !d.Part.TryGetTarget(out _));
        Dims.Add((new WeakReference<UIElement>(part), opacity));
        part.Opacity = IsHighContrast ? 1 : opacity;
        return part;
    }

    /// <summary>Paints now, and whenever high contrast or its colors change.</summary>
    public static void Follow(Action<bool> paint)
    {
        Painters.Add(paint);
        paint(IsHighContrast);
    }

    /// <summary>
    /// In high contrast, the parts of a window that are drawn in another color than the system's, as they are drawn (CI's
    /// scenarios ask), its open dialogs and menus too; none without it. What was looked at is counted: none is not a pass.
    /// A picture, and the fill of what has the tag "art", has its own colors.
    /// </summary>
    public static (int Checked, string Strays) Strays(UIElement root)
    {
        if (!IsHighContrast) return (0, "");
        var seen = 0;
        var system = Enum.GetValues<UIElementType>().Select(t => Settings.UIElementColor(t)).Select(c => (c.R, c.G, c.B)).ToHashSet();
        var strays = new List<string>();

        void Look(string part, Brush? brush)
        {
            var colors = brush switch
            {
                SolidColorBrush solid => new[] { solid.Color },
                AcrylicBrush acrylic => new[] { acrylic.FallbackColor, acrylic.TintColor },
                RadialGradientBrush radial => radial.GradientStops.Select(s => s.Color).ToArray(),
                GradientBrush gradient => gradient.GradientStops.Select(s => s.Color).ToArray(),
                _ => Array.Empty<Color>(),
            };
            seen += colors.Length;
            foreach (var c in colors.Where(c => c.A > 0 && !system.Contains((c.R, c.G, c.B))).Take(1)) strays.Add($"{part} #{c.R:X2}{c.G:X2}{c.B:X2}");
        }

        void Walk(DependencyObject part, string path, double opacity = 1)
        {
            opacity *= (part as UIElement)?.Opacity ?? 1;
            // The dimming under a dialog is the framework's
            if (part is FrameworkElement { Name: "SmokeLayerBackground" } or UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: 0 }) return;
            var here = part is FrameworkElement { Name.Length: > 0 } named ? path + "/" + named.Name : path + "/" + part.GetType().Name;
            if (here.Length > 90) here = "…" + here[^90..];
            switch (part)
            {
                case Border { Tag: "art" } picture:
                    if (picture.BorderThickness != default) Look(here + " edge", picture.BorderBrush);
                    return;
                case Border border:
                    Look(here + " fill", border.Background);
                    if (border.BorderThickness != default) Look(here + " edge", border.BorderBrush);
                    break;
                case TextBlock text when text.Text.Length > 0:
                    Look(here + " text '" + text.Text + "'", text.Foreground);
                    if (opacity < 0.99) strays.Add($"{here} text '{text.Text}' opacity");
                    break;
                case FontIcon icon:
                    Look(here + " icon", icon.Foreground);
                    if (opacity < 0.99) strays.Add(here + " icon opacity");
                    break;
                case Grid grid:
                    Look(here + " fill", grid.Background);
                    if (grid.BorderThickness != default) Look(here + " edge", grid.BorderBrush);
                    break;
                case Panel panel: Look(here + " fill", panel.Background); break;
                case ContentPresenter presenter:
                    Look(here + " fill", presenter.Background);
                    if (presenter.BorderThickness != default) Look(here + " edge", presenter.BorderBrush);
                    break;
                case Shape shape:
                    Look(here + " fill", shape.Fill);
                    if (shape.StrokeThickness > 0) Look(here + " line", shape.Stroke);
                    break;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(part); i++) Walk(VisualTreeHelper.GetChild(part, i), here, opacity);
        }

        Walk(root, "");
        if (root.XamlRoot is { } xaml)
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xaml)) if (popup.Child is { } child) Walk(child, "popup");
        }

        return (seen, string.Join("; ", strays.Distinct().Take(12)));
    }
}
