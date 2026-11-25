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

        FollowHighContrast(app, app.RequestedTheme == ApplicationTheme.Dark);

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

    /// <summary>
    /// The Studio's own brushes follow high contrast, also as it is turned on or off, or its colors change, while Studio
    /// runs. There is one brush for a name, in the app's theme and in high contrast, and it is painted anew: what is on
    /// screen has it already. Without it their names did not exist there, and their text was not drawn.
    /// </summary>
    private static void FollowHighContrast(Application app, bool dark)
    {
        var theme = Theme(app, dark ? "Dark" : "Light");
        var high = Theme(app, "HighContrast");
        var repaint = new List<Action<bool>>();
        foreach (var (key, role) in HighContrast.Roles)
        {
            high[key] = theme[key];
            repaint.Add(Painter((Brush)theme[key], role));
        }

        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Paint()
        {
            IsHighContrast = Accessibility.HighContrast;
            foreach (var paint in repaint) paint(IsHighContrast);
        }

        Paint();
        Settings.ColorValuesChanged += (_, _) => ui.TryEnqueue(Paint);
    }

    /// <summary>High contrast is on, and the Studio's brushes are in its colors.</summary>
    public static bool IsHighContrast { get; private set; }

    /// <summary>Brushes that were asked for and are not there (CI's scenarios ask).</summary>
    public static int Missing { get; private set; }

    /// <summary>Whether the Studio's text and cards are the system's colors, or no high contrast is on.</summary>
    public static bool IsPainted =>
        !IsHighContrast || (Res("StudioLinkBrush") is SolidColorBrush link && link.Color == SystemColor(HighContrastRole.Link)
            && Res("StudioCardBrush") is AcrylicBrush card && card.FallbackColor == SystemColor(HighContrastRole.Window));

    private static readonly global::Windows.UI.ViewManagement.UISettings Settings = new();
    private static readonly global::Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();

    private static Color SystemColor(HighContrastRole role) => role switch
    {
        HighContrastRole.Window => Settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window),
        HighContrastRole.Link => Settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Hotlight),
        HighContrastRole.Highlight => Settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Highlight),
        HighContrastRole.HighlightText => Settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.HighlightText),
        HighContrastRole.None => Colors.Transparent,
        _ => Settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText),
    };

    /// <summary>Paints one brush in the system's color, or again as the theme has it.</summary>
    private static Action<bool> Painter(Brush brush, HighContrastRole role)
    {
        switch (brush)
        {
            case SolidColorBrush solid:
            {
                var color = solid.Color;
                return high => solid.Color = high ? SystemColor(role) : color;
            }

            case AcrylicBrush acrylic:
            {
                var (tint, opacity, luminosity, fallback) = (acrylic.TintColor, acrylic.TintOpacity, acrylic.TintLuminosityOpacity, acrylic.FallbackColor);
                return high =>
                {
                    acrylic.TintColor = high ? SystemColor(role) : tint;
                    acrylic.TintOpacity = high ? 1 : opacity;
                    acrylic.TintLuminosityOpacity = high ? 1 : luminosity;
                    acrylic.FallbackColor = high ? SystemColor(role) : fallback;
                };
            }

            case GradientBrush gradient:
            {
                var colors = gradient.GradientStops.Select(s => s.Color).ToList();
                return high =>
                {
                    for (var i = 0; i < colors.Count; i++) gradient.GradientStops[i].Color = high ? SystemColor(role) : colors[i];
                };
            }

            default:
                throw new InvalidOperationException("no painter for " + brush.GetType().Name);
        }
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
        // The picked card is opaque: its glow is behind it and would show through, under its text
        t["StudioPickedCardBrush"] = Gradient(Over(primary, surface, StudioTheme.PickedTop), Over(primary, surface, StudioTheme.PickedBottom));
        t["StudioPickedEdgeBrush"] = B(Gold);
        t["StudioTextBrush"] = B(text);
        t["StudioMutedBrush"] = B(muted);
        // The app's own name: under the framework's the merged WinUI dictionary answers first, with the accent's shade
        t["StudioLinkBrush"] = B(link);
        t["StudioOnGoldBrush"] = B(onPrimary);
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

    private static uint Argb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static Color Over(Color color, Color surface, double alpha)
    {
        var v = StudioTheme.Over(Argb(color), Argb(surface), alpha);
        return Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>
    /// Whether text of one brush can be read on the other, everywhere on it (CI's scenarios ask). Not where something
    /// behind it shows through, or where it is not known what is drawn.
    /// </summary>
    public static bool IsReadable(Brush? text, Brush? on)
    {
        var colors = on switch
        {
            SolidColorBrush solid => new[] { solid.Color },
            AcrylicBrush acrylic => new[] { acrylic.FallbackColor },
            RadialGradientBrush radial => radial.GradientStops.Select(s => s.Color).ToArray(),
            GradientBrush gradient => gradient.GradientStops.Select(s => s.Color).ToArray(),
            _ => Array.Empty<Color>(),
        };
        return text is SolidColorBrush { Color.A: 255 } ink && colors.Length > 0
            && colors.All(c => c.A == 255 && StudioTheme.Contrast(Argb(ink.Color), Argb(c)) >= 4.5);
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

        // The dialog's own state gives its default button the gold style, with the framework's white text (seen in the
        // frames): the button itself has ours.
        var onGold = Res("StudioOnGoldBrush");
        foreach (var state in new[] { "", "PointerOver", "Pressed" }) dialog.Resources["AccentButtonForeground" + state] = onGold;
        // Not Opened: it may come before the dialog has any of its parts (the edit dialog, whose field takes the focus)
        dialog.Loaded += (_, _) =>
        {
            if (dialog.DefaultButton == ContentDialogButton.None) return;
            if (DefaultButton(dialog) is not { } button)
            {
                UnreadableDialogs++;
                return;
            }

            button.Foreground = onGold;
            dialog.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                CheckedDialogs++;
                // What cannot be read is counted: it is not known to be drawn in ours
                if (!IsHighContrast && (onGold is not SolidColorBrush ours || Presenter(button)?.Foreground is not SolidColorBrush shown || shown.Color != ours.Color)) UnreadableDialogs++;
            });
        };
        return dialog;
    }

    /// <summary>Dialogs whose default button was not drawn in the text color for gold (CI's scenarios ask).</summary>
    public static int UnreadableDialogs { get; private set; }

    /// <summary>Dialogs whose default button was looked at.</summary>
    public static int CheckedDialogs { get; private set; }

    private static Button? DefaultButton(ContentDialog dialog) => dialog.DefaultButton switch
    {
        ContentDialogButton.Primary => Find<Button>(dialog, "PrimaryButton"),
        ContentDialogButton.Secondary => Find<Button>(dialog, "SecondaryButton"),
        ContentDialogButton.Close => Find<Button>(dialog, "CloseButton"),
        _ => null,
    };

    public static ContentPresenter? Presenter(Button button) => Find<ContentPresenter>(button, "ContentPresenter");

    private static T? Find<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && found.Name == name) return found;
            if (Find<T>(child, name) is { } deeper) return deeper;
        }

        return null;
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
            Missing++;
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

    /// <summary>A picture with rounded corners. It never mirrors: a right-to-left layout would flip the brush.</summary>
    public static Border Picture(ImageSource source, CornerRadius corners) => new()
    {
        CornerRadius = corners,
        FlowDirection = FlowDirection.LeftToRight,
        Background = new ImageBrush { ImageSource = source, Stretch = Stretch.UniformToFill },
    };

    private const string AvatarTag = "avatar";

    /// <summary>What is on the page of pictures and avatars, and how many are mirrored or have initials that cannot be read (CI's scenarios ask).</summary>
    public static (int Pictures, int Mirrored, int Avatars, int AvatarPictures, int Unreadable) Look(DependencyObject root)
    {
        var (pictures, mirrored, avatars, avatarPictures, unreadable) = (0, 0, 0, 0, 0);
        foreach (var part in Descendants(root))
        {
            if (part is Border { Background: ImageBrush, ActualWidth: > 0 } picture)
            {
                pictures++;
                if (picture.FlowDirection != FlowDirection.LeftToRight) mirrored++;
            }

            if (part is Grid { Tag: AvatarTag, ActualWidth: > 0 } face)
            {
                avatars++;
                if (face.Children.OfType<Border>().Any(b => b.Background is ImageBrush)) avatarPictures++;
                if (!IsReadable(face.Children.OfType<TextBlock>().FirstOrDefault()?.Foreground, face.Children.OfType<Border>().FirstOrDefault()?.Background)) unreadable++;
            }
        }

        return (pictures, mirrored, avatars, avatarPictures, unreadable);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }

    /// <summary>Round logo: initials on gold (a channel) or teal (a person) until the picture loads; gold ring.</summary>
    public static FrameworkElement Avatar(string? initials, string? url, double size, bool person)
    {
        var face = new Grid { Width = size, Height = size, Tag = AvatarTag };
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
            var picture = Picture(new BitmapImage(u) { DecodePixelWidth = (int)(size * 2) }, new CornerRadius(size / 2));
            picture.Margin = new Thickness(2);
            face.Children.Add(picture);
        }

        return face;
    }
}
