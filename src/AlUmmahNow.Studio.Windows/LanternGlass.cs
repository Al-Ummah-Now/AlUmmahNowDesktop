using System.Numerics;
using AlUmmahNow.Studio.Shell;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// Lantern Glass on WinUI 3: the Fawanees colours from the shared native theme registry (vendored
/// <c>Fanous.Colors.xaml</c>), turned into the Studio's brushes. Gold replaces the Windows accent
/// (primary buttons, toggles, focus, the selected nav item); the sidebar and cards are in-app Acrylic
/// over the window's Mica; the header band is night blue in Light and Dark.
/// </summary>
internal static class LanternGlass
{
    /// <summary>Night blue of the header band, in both themes.</summary>
    public static readonly Color Night = Color.FromArgb(0xFF, 0x09, 0x15, 0x1E);
    public static readonly Color NightTop = Color.FromArgb(0xFF, 0x0B, 0x1D, 0x2A);
    public static readonly Color OnNight = Color.FromArgb(0xFF, 0xF7, 0xF1, 0xE8);
    public static readonly Color OnNightMuted = Color.FromArgb(0xFF, 0xC9, 0xD5, 0xDA);
    public static readonly Color Gold = Color.FromArgb(0xFF, 0xF4, 0xAE, 0x3A);
    public static readonly Color GoldEdge = Color.FromArgb(0xFF, 0xFF, 0xD9, 0x8A);

    /// <summary>Loads the Fawanees colours and the Studio brushes into the application resources.</summary>
    public static void Apply(Application app)
    {
        // The generated dictionary is read as XML (its Color entries), not through XamlReader: the
        // colours are all the Studio needs, and plain parsing can't fail on markup the runtime rejects.
        var themes = ReadColors(StudioTheme.Fanous("Fanous.Colors.xaml"));
        // Fanous names its dark dictionary "Default" (the brand is dark-first); WinUI asks for "Dark".
        Fill(Theme(app, "Dark"), themes["Default"], dark: true);
        Fill(Theme(app, "Light"), themes["Light"], dark: false);

        HighContrast(Theme(app, "HighContrast"));

        // Text and check marks on gold (the framework's are white in light mode, 1.9:1 on it), and links (the accent's
        // shade, 2.4:1). In a dictionary merged after the framework's own, which answers before the application's theme
        // dictionaries. High contrast keeps the framework's.
        var controls = new ResourceDictionary();
        foreach (var (theme, source) in new[] { ("Dark", "Default"), ("Light", "Light") })
        {
            var onGold = new SolidColorBrush(themes[source]["FanousOnPrimaryColor"]);
            var link = new SolidColorBrush(themes[source]["FanousLinkColor"]);
            var d = new ResourceDictionary();
            foreach (var state in new[] { "", "PointerOver", "Pressed" })
            {
                d["HyperlinkButtonForeground" + state] = link;
                d["AccentButtonForeground" + state] = onGold;
                d["CheckBoxCheckGlyphForegroundChecked" + state] = onGold;
                d["CheckBoxCheckGlyphForegroundIndeterminate" + state] = onGold;
            }

            controls.ThemeDictionaries[theme] = d;
        }

        app.Resources.MergedDictionaries.Add(controls);
    }

    /// <summary>The Studio's own brushes in the system's high-contrast colors: without them its text was not drawn.</summary>
    private static void HighContrast(ResourceDictionary t)
    {
        var system = new global::Windows.UI.ViewManagement.UISettings();
        SolidColorBrush B(global::Windows.UI.ViewManagement.UIElementType e) => new(system.UIElementColor(e));
        foreach (var key in new[] { "Muted", "Danger", "SelectedText", "Edge", "EdgeStrong" }) t["Studio" + key + "Brush"] = B(global::Windows.UI.ViewManagement.UIElementType.WindowText);
        foreach (var key in new[] { "Page", "Sidebar", "Card", "Menu" }) t["Studio" + key + "Brush"] = B(global::Windows.UI.ViewManagement.UIElementType.Window);
        foreach (var key in new[] { "Sheen", "Selected" }) t["Studio" + key + "Brush"] = new SolidColorBrush(Colors.Transparent);
        t["StudioLinkBrush"] = B(global::Windows.UI.ViewManagement.UIElementType.Hotlight);
        t["StudioDangerFillBrush"] = B(global::Windows.UI.ViewManagement.UIElementType.Highlight);
        t["StudioOnDangerFillBrush"] = B(global::Windows.UI.ViewManagement.UIElementType.HighlightText);
    }

    /// <summary>Theme key ("Default", "Light") → colour key → colour, from Fanous.Colors.xaml.</summary>
    private static Dictionary<string, Dictionary<string, Color>> ReadColors(string xaml) =>
        StudioTheme.ReadXamlColors(xaml).ToDictionary(
            t => t.Key,
            t => t.Value.ToDictionary(c => c.Key, c => Color.FromArgb((byte)(c.Value >> 24), (byte)(c.Value >> 16), (byte)(c.Value >> 8), (byte)c.Value)));

    private static ResourceDictionary Theme(Application app, string key)
    {
        if (app.Resources.ThemeDictionaries.TryGetValue(key, out var existing) && existing is ResourceDictionary d) return d;
        var created = new ResourceDictionary();
        app.Resources.ThemeDictionaries[key] = created;
        return created;
    }

    private static void Fill(ResourceDictionary t, Dictionary<string, Color> f, bool dark)
    {
        Color C(string name) => f["Fanous" + name + "Color"];
        Color A(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
        SolidColorBrush B(Color c) => new(c);

        var primary = C("Primary");
        var onPrimary = C("OnPrimary");
        var surface = C("Surface");
        var text = C("TextPrimary");
        var text2 = C("TextSecondary");
        var muted = C("TextMuted");
        var link = C("Link");
        var edge = C("PrimaryEdge");

        // The Windows accent becomes lantern gold: AccentButtonStyle, checkboxes, toggles, sliders, progress.
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorDark1" }) t[key] = primary;
        t["SystemAccentColorLight2"] = C("PrimaryHover");
        t["SystemAccentColorLight3"] = C("PrimarySheen");
        t["SystemAccentColorDark2"] = C("PrimaryPressed");
        t["SystemAccentColorDark3"] = C("PrimaryPressed");
        t["AccentFillColorDefaultBrush"] = Gradient(C("PrimaryHover"), primary);
        t["AccentFillColorSecondaryBrush"] = B(C("PrimaryHover"));
        t["AccentFillColorTertiaryBrush"] = B(C("PrimaryPressed"));
        t["AccentFillColorDisabledBrush"] = B(C("PrimaryDisabled"));
        t["AccentTextFillColorPrimaryBrush"] = B(link);
        t["AccentTextFillColorSecondaryBrush"] = B(link);
        t["AccentTextFillColorTertiaryBrush"] = B(link);
        t["TextOnAccentFillColorPrimaryBrush"] = B(onPrimary);
        t["TextOnAccentFillColorSecondaryBrush"] = B(onPrimary);
        t["TextOnAccentFillColorDisabledBrush"] = B(C("OnPrimaryDisabled"));
        t["AccentControlElevationBorderBrush"] = B(dark ? GoldEdge : edge);
        t["HyperlinkForeground"] = B(link);
        t["FocusStrokeColorOuterBrush"] = B(C("FocusRing"));

        // Warm text from the tokens instead of Windows' neutral greys.
        t["TextFillColorPrimaryBrush"] = B(text);
        t["TextFillColorSecondaryBrush"] = B(text2);
        t["TextFillColorTertiaryBrush"] = B(muted);

        // Studio brushes (StudioWindow, Card).
        // The page: the registry's page material, one solid colour (as the web's Fawanees page).
        t["StudioPageBrush"] = B(LanternGlass.C(FawaneesHeader.Page(dark)));
        t["StudioSidebarBrush"] = new AcrylicBrush
        {
            TintColor = dark ? NightTop : C("Background"),
            TintOpacity = dark ? 0.62 : 0.55,
            TintLuminosityOpacity = dark ? 0.85 : 0.9,
            FallbackColor = dark ? NightTop : C("Background"),
        };
        t["StudioCardBrush"] = new AcrylicBrush
        {
            TintColor = surface,
            TintOpacity = dark ? 0.6 : 0.7,
            TintLuminosityOpacity = dark ? 0.8 : 0.95,
            FallbackColor = surface,
        };
        t["StudioMenuBrush"] = new AcrylicBrush { TintColor = C("SurfaceRaised"), TintOpacity = 0.78, TintLuminosityOpacity = 0.9, FallbackColor = C("SurfaceRaised") };
        t["StudioEdgeBrush"] = B(dark ? A(GoldEdge, 0x2E) : A(edge, 0x38));
        t["StudioEdgeStrongBrush"] = B(dark ? A(GoldEdge, 0x66) : A(edge, 0x70));
        t["StudioSheenBrush"] = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = A(GoldEdge, dark ? (byte)0x1A : (byte)0x26), Offset = 0 },
                new GradientStop { Color = A(GoldEdge, 0), Offset = 0.35 },
            },
        };
        t["StudioSelectedBrush"] = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
            GradientStops =
            {
                new GradientStop { Color = A(primary, 0x40), Offset = 0 },
                new GradientStop { Color = A(primary, 0x14), Offset = 1 },
            },
        };
        t["StudioSelectedTextBrush"] = B(dark ? Color.FromArgb(0xFF, 0xFF, 0xC4, 0x6B) : Color.FromArgb(0xFF, 0x6B, 0x3B, 0x00));
        t["StudioMutedBrush"] = B(muted);
        // The app's own name: under the framework's the merged WinUI dictionary answers first, with the accent's shade
        t["StudioLinkBrush"] = B(link);
        t["StudioDangerBrush"] = B(C("ErrorText"));
        t["StudioDangerFillBrush"] = B(C("ErrorFill"));
        t["StudioOnDangerFillBrush"] = B(C("OnErrorFill"));

        // NavigationView: Acrylic pane, transparent content (the Studio page brush sits over Mica), gold selection.
        t["NavigationViewExpandedPaneBackground"] = t["StudioSidebarBrush"];
        t["NavigationViewDefaultPaneBackground"] = t["StudioSidebarBrush"];
        t["NavigationViewTopPaneBackground"] = t["StudioSidebarBrush"];
        t["NavigationViewContentBackground"] = new SolidColorBrush(Colors.Transparent);
        t["NavigationViewContentGridBorderBrush"] = t["StudioEdgeBrush"];
        t["NavigationViewSelectionIndicatorForeground"] = B(primary);
        t["NavigationViewItemBackgroundSelected"] = t["StudioSelectedBrush"];
        t["NavigationViewItemBackgroundSelectedPointerOver"] = t["StudioSelectedBrush"];
        t["NavigationViewItemBackgroundSelectedPressed"] = t["StudioSelectedBrush"];
        t["NavigationViewItemForegroundSelected"] = t["StudioSelectedTextBrush"];
        t["NavigationViewItemForegroundSelectedPointerOver"] = t["StudioSelectedTextBrush"];

        // The sidebar's page list (a ListView): gold-tinted selection with the gold pill.
        t["ListViewItemBackgroundSelected"] = t["StudioSelectedBrush"];
        t["ListViewItemBackgroundSelectedPointerOver"] = t["StudioSelectedBrush"];
        t["ListViewItemBackgroundSelectedPressed"] = t["StudioSelectedBrush"];
        t["ListViewItemForegroundSelected"] = t["StudioSelectedTextBrush"];
        t["ListViewItemSelectionIndicatorBrush"] = B(primary);
        t["ListViewItemSelectionIndicatorPointerOverBrush"] = B(primary);
        t["ListViewItemSelectionIndicatorPressedBrush"] = B(primary);

        // Menus and flyouts: frosted, gold-edged.
        t["MenuFlyoutPresenterBackground"] = t["StudioMenuBrush"];
        t["FlyoutPresenterBackground"] = t["StudioMenuBrush"];
        t["ContentDialogBackground"] = t["StudioMenuBrush"];
        t["ContentDialogBorderBrush"] = t["StudioEdgeStrongBrush"];
    }

    private static LinearGradientBrush Gradient(Color top, Color bottom) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops = { new GradientStop { Color = top, Offset = 0 }, new GradientStop { Color = bottom, Offset = 1 } },
    };

    /// <summary>
    /// A dialog in Lantern Glass: the frosted menu surface with a gold edge and rounded corners. A
    /// destructive action (Delete, Sign out, Cancel upload) is red and Cancel stays the default; any
    /// other action is the gold default button.
    /// </summary>
    public static ContentDialog Dialog(ContentDialog dialog, bool destructive)
    {
        dialog.Background = Res("StudioMenuBrush");
        dialog.BorderBrush = Res("StudioEdgeStrongBrush");
        dialog.BorderThickness = new Thickness(1);
        dialog.CornerRadius = new CornerRadius(14);
        if (destructive && !string.IsNullOrEmpty(dialog.PrimaryButtonText))
        {
            var danger = new Style(typeof(Button)) { BasedOn = (Style)Application.Current.Resources["DefaultButtonStyle"] };
            danger.Setters.Add(new Setter(Control.BackgroundProperty, Res("StudioDangerFillBrush")));
            danger.Setters.Add(new Setter(Control.ForegroundProperty, Res("StudioOnDangerFillBrush")));
            danger.Setters.Add(new Setter(Control.BorderBrushProperty, Res("StudioDangerFillBrush")));
            dialog.PrimaryButtonStyle = danger;
            dialog.DefaultButton = ContentDialogButton.Close;
        }
        else if (!string.IsNullOrEmpty(dialog.PrimaryButtonText))
        {
            dialog.DefaultButton = ContentDialogButton.Primary;
        }

        return dialog;
    }

    /// <summary>A Studio brush for the app's theme; transparent if the theme failed to load (never a crash).</summary>
    public static Brush Res(string key)
    {
        try
        {
            return (Brush)Application.Current.Resources[key];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is KeyNotFoundException || ex is InvalidCastException)
        {
            return new SolidColorBrush(Colors.Transparent);
        }
    }

    public static Color C(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>
    /// The header band, colour for colour the web Fawanees header (<see cref="FawaneesHeader"/>): a
    /// 90° gradient nav-bg → (nav-bg 84 % + primary) → nav-bg (no art, no glow, as on the web), and at
    /// the bottom the nav-border line over the gold rule (primary at 66 %).
    /// </summary>
    public static Grid NightBand(FawaneesHeader h)
    {
        var band = new Grid
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0.5),
                EndPoint = new Windows.Foundation.Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop { Color = C(h.Background), Offset = 0 },
                    new GradientStop { Color = C(h.Middle), Offset = 0.5 },
                    new GradientStop { Color = C(h.Background), Offset = 1 },
                },
            },
        };
        var rules = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        rules.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(C(h.Border)) });
        rules.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(C(h.GoldRule)) });
        band.Children.Add(rules);
        // Never clip outside the band.
        band.SizeChanged += (_, e) => band.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        return band;
    }

    /// <summary>
    /// A frosted card: Acrylic surface, gold-tinted edge, a soft sheen at the top and a gold glow
    /// (a coloured composition drop shadow: ThemeShadow is grey only). Buttons go in <paramref name="footer"/>,
    /// end-aligned.
    /// </summary>
    public static FrameworkElement Card(UIElement body, UIElement? footer = null, double radius = 14)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new Border { Background = Res("StudioSheenBrush"), CornerRadius = new CornerRadius(radius), IsHitTestVisible = false });
        Grid.SetRowSpan((FrameworkElement)grid.Children[0], 2);
        var content = new Border { Child = body, Padding = new Thickness(18, 14, 18, 16) };
        grid.Children.Add(content);
        if (footer != null)
        {
            var bar = new Border { BorderBrush = Res("StudioEdgeBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 10), Child = footer };
            if (footer is FrameworkElement f) f.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(bar, 1);
            grid.Children.Add(bar);
        }

        var card = new Border
        {
            Background = Res("StudioCardBrush"),
            BorderBrush = Res("StudioEdgeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Child = grid,
        };
        return WithGlow(card, radius);
    }

    /// <summary>Puts a gold glow behind <paramref name="element"/>.</summary>
    public static FrameworkElement WithGlow(FrameworkElement element, double radius = 14, float opacity = 0.32f, float blur = 26f)
    {
        // The host takes the element's place (alignment, margin), so the glow sits exactly behind it
        // (a centred element in a stretched host left the glow at the host's top-left corner).
        var host = new Grid
        {
            HorizontalAlignment = element.HorizontalAlignment,
            VerticalAlignment = element.VerticalAlignment,
            Margin = element.Margin,
        };
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        element.VerticalAlignment = VerticalAlignment.Stretch;
        element.Margin = new Thickness(0);
        var shadowHost = new Canvas { IsHitTestVisible = false };
        host.Children.Add(shadowHost);
        host.Children.Add(element);
        var compositor = ElementCompositionPreview.GetElementVisual(shadowHost).Compositor;
        var sprite = compositor.CreateSpriteVisual();
        var shadow = compositor.CreateDropShadow();
        shadow.Color = Gold;
        shadow.BlurRadius = blur;
        shadow.Opacity = opacity;
        shadow.Offset = new Vector3(0, 8, 0);
        // A rounded mask keeps the glow's corners matching the card's.
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2((float)radius);
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = compositor.CreateColorBrush(Colors.Black);
        var shapes = compositor.CreateShapeVisual();
        shapes.Shapes.Add(shape);
        var maskSurface = compositor.CreateVisualSurface();
        maskSurface.SourceVisual = shapes;
        shadow.Mask = compositor.CreateSurfaceBrush(maskSurface);
        sprite.Shadow = shadow;
        element.SizeChanged += (_, e) =>
        {
            var size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            sprite.Size = size;
            shapes.Size = size;
            geometry.Size = size;
            maskSurface.SourceSize = size;
        };
        ElementCompositionPreview.SetElementChildVisual(shadowHost, sprite);
        return host;
    }

    /// <summary>Round logo: initials on gold (a channel) or teal (a person) until the picture loads; gold ring.</summary>
    public static FrameworkElement Avatar(string? initials, string? url, double size, bool person)
    {
        var face = new Grid { Width = size, Height = size };
        face.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size / 2),
            Background = person
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0x75, 0x8C))
                : new RadialGradientBrush
                {
                    GradientOrigin = new Windows.Foundation.Point(0.3, 0.25),
                    Center = new Windows.Foundation.Point(0.5, 0.5),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0xE6, 0xA8), Offset = 0 },
                        new GradientStop { Color = Gold, Offset = 0.55 },
                        new GradientStop { Color = Color.FromArgb(0xFF, 0xB0, 0x7A, 0x22), Offset = 1 },
                    },
                },
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, GoldEdge.R, GoldEdge.G, GoldEdge.B)),
        });
        face.Children.Add(new TextBlock
        {
            Text = initials ?? "",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = size * 0.38,
            Foreground = new SolidColorBrush(person ? Colors.White : Night),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = FlowDirection.LeftToRight,
        });
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            face.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(size / 2),
                Margin = new Thickness(2),
                Background = new ImageBrush { ImageSource = new BitmapImage(u) { DecodePixelWidth = (int)(size * 2) }, Stretch = Stretch.UniformToFill },
            });
        }

        return face;
    }
}
