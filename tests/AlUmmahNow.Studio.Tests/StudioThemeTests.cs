using AlUmmahNow.Studio.Shell;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>Lantern Glass assets ship inside the Studio assembly, from the shared Fawanees registry.</summary>
public sealed class StudioThemeTests
{
    [Theory]
    [InlineData("Fanous.Colors.xaml", "FanousPrimaryColor")]
    [InlineData("Fanous.Tokens.xaml", "FanousRadiusLg")]
    [InlineData("style.css", "@define-color fanous_primary #f4ae3a")]
    [InlineData("style-dark.css", "@define-color fanous_primary #f4ae3a")]
    public void FawaneesTokensAreBundled(string file, string expected) => Assert.Contains(expected, StudioTheme.Fanous(file));

    [Fact]
    public void BothThemesAreInTheWinUiColours()
    {
        var colors = StudioTheme.Fanous("Fanous.Colors.xaml");
        Assert.Contains("x:Key=\"Default\"", colors);
        Assert.Contains("x:Key=\"Light\"", colors);
    }

    [Fact]
    public void WinUiColoursReadAsXmlForBothThemes()
    {
        var themes = StudioTheme.ReadXamlColors(StudioTheme.Fanous("Fanous.Colors.xaml"));
        Assert.Equal(0xFFF4AE3Au, themes["Default"]["FanousPrimaryColor"]);
        Assert.Equal(0xFF09151Eu, themes["Default"]["FanousBackgroundColor"]);
        Assert.Equal(0xFFF3EEE5u, themes["Light"]["FanousBackgroundColor"]);
        // Every colour the Windows Studio reads exists in both themes.
        foreach (var name in new[] { "Primary", "OnPrimary", "Surface", "SurfaceRaised", "Background", "TextPrimary", "TextSecondary", "TextMuted", "Link", "PrimaryEdge", "PrimaryHover", "PrimarySheen", "PrimaryPressed", "PrimaryDisabled", "OnPrimaryDisabled", "FocusRing", "ErrorText" })
        {
            Assert.Contains("Fanous" + name + "Color", themes["Default"].Keys);
            Assert.Contains("Fanous" + name + "Color", themes["Light"].Keys);
        }
    }

    [Fact]
    public void GtkStylesUseTheTokensAndTheLanternArt()
    {
        var css = StudioTheme.Studio("glass.css");
        Assert.Contains("@fanous_primary", css);
        Assert.Contains("headerbar.night", css);
    }

    private static double Contrast(uint a, uint b) => StudioTheme.Contrast(a, b);

    [Fact]
    public void ContrastIsWcags()
    {
        Assert.Equal(21, Contrast(0x000000, 0xFFFFFF), 3);
        Assert.Equal(1, Contrast(0xF4AE3A, 0xF4AE3A), 3);
        Assert.Equal(4.48, Contrast(0x777777, 0xFFFFFF), 2);
    }

    [Fact]
    public void OverIsOneOpaqueColor()
    {
        Assert.Equal(0xFFFF8000u, StudioTheme.Over(0xFFFF8000, 0xFF123456, 1));
        Assert.Equal(0xFF123456u, StudioTheme.Over(0xFFFF8000, 0xFF123456, 0));
        Assert.Equal(0xFF7F7F7Fu, StudioTheme.Over(0xFFFEFEFE, 0xFF000000, 0.5));
    }

    /// <summary>The picked card of Analytics, on Windows and Linux: its label and its value, at its top and bottom.</summary>
    [Theory]
    [InlineData("Default")]
    [InlineData("Light")]
    public void ThePickedCardIsReadable(string theme)
    {
        var c = StudioTheme.ReadXamlColors(StudioTheme.Fanous("Fanous.Colors.xaml"))[theme];
        foreach (var text in new[] { "TextPrimary", "TextMuted" })
        {
            foreach (var alpha in new[] { StudioTheme.PickedTop, StudioTheme.PickedBottom })
            {
                var on = StudioTheme.Over(c["FanousPrimaryColor"], c["FanousSurfaceColor"], alpha);
                Assert.True(Contrast(c["Fanous" + text + "Color"], on) >= 4.5, theme + ": " + text + " on the picked card");
            }
        }

        Assert.Contains(FormattableString.Invariant($"linear-gradient(to bottom, alpha(@fanous_primary, {StudioTheme.PickedTop}), alpha(@fanous_primary, {StudioTheme.PickedBottom}))"), StudioTheme.Studio("glass.css"));
    }

    /// <summary>The white initials of an account, on every color of the circle.</summary>
    [Fact]
    public void AnAccountsInitialsAreReadable()
    {
        var person = System.Text.RegularExpressions.Regex.Match(StudioTheme.Studio("glass.css"), @"\.studio-avatar\.person \{\s*background-image: radial-gradient\(([^;]+)\);\s*color: white;");
        Assert.True(person.Success);
        var stops = System.Text.RegularExpressions.Regex.Matches(person.Groups[1].Value, "#([0-9a-f]{6})");
        Assert.Equal(3, stops.Count);
        Assert.All(stops, s => Assert.True(Contrast(0xFFFFFF, Convert.ToUInt32(s.Groups[1].Value, 16)) >= 4.5, s.Value));
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Light")]
    public void TextTheStudioDrawsIsReadable(string theme)
    {
        var c = StudioTheme.ReadXamlColors(StudioTheme.Fanous("Fanous.Colors.xaml"))[theme];
        // Edit, dimmed lines, Delete; and the text on gold, at rest, hovered and pressed
        foreach (var (text, on) in new[] { ("Link", "Surface"), ("TextMuted", "Surface"), ("TextMuted", "Background"), ("ErrorText", "Surface"), ("OnPrimary", "Primary"), ("OnPrimary", "PrimaryHover"), ("OnPrimary", "PrimaryPressed"), ("OnErrorFill", "ErrorFill") })
        {
            Assert.True(Contrast(c["Fanous" + text + "Color"], c["Fanous" + on + "Color"]) >= 4.5, theme + ": " + text + " on " + on);
        }

        var h = FawaneesHeader.For(theme != "Light");
        static uint U(Rgba v) => ((uint)v.R << 16) | ((uint)v.G << 8) | v.B;
        Assert.True(Contrast(U(h.SearchPlaceholder), U(h.SearchBackground)) >= 4.5, theme + ": search placeholder");
    }

    /// <summary>A toolkit dims, or answers for, colors of its own unless told otherwise: what made them unreadable in light mode.</summary>
    [Fact]
    public void TheToolkitsOwnColorsDoNotWin()
    {
        var css = StudioTheme.Studio("glass.css");
        Assert.Contains(".dim-label { opacity: 1; color: @fanous_text_muted; }", css);
        Assert.Contains("entry > text > placeholder, entry > placeholder { opacity: 1; color: @fanous_text_muted; }", css);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AlUmmahNow.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string Source(string project, string file) => File.ReadAllText(Path.Combine(root!.FullName, "src", project, file));
        Assert.Contains("header-search entry > text > placeholder {{ color: {h.SearchPlaceholder.Css}; opacity: 1; }}", Source("AlUmmahNow.Studio.Linux", "StudioStyle.cs"));
        // WinUI's dictionary is merged into the application's and answers first for a name of its own
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "AlUmmahNow.Studio.Windows"), "*.cs"))
        {
            Assert.DoesNotMatch("LanternGlass\\.Res\\(\"(?!Studio)", File.ReadAllText(file));
        }

        Assert.Contains("PlaceholderForeground = new SolidColorBrush(LanternGlass.C(h.SearchPlaceholder))", Source("AlUmmahNow.Studio.Windows", "StudioWindow.cs"));
        Assert.Contains("TextControlPlaceholderForeground\" + state] = new SolidColorBrush(LanternGlass.C(h.SearchPlaceholder))", Source("AlUmmahNow.Studio.Windows", "StudioWindow.cs"));
        var glass = Source("AlUmmahNow.Studio.Windows", "LanternGlass.cs");
        Assert.Contains("app.Resources.MergedDictionaries.Add(controls);", glass);
        Assert.Contains("d[\"HyperlinkButtonForeground\" + state] = link;", glass);
        Assert.Contains("            button.Foreground = onGold;", glass);
        Assert.Contains("dialog button.destructive, dialog button.destructive-action {\n  background-color: @fanous_error_fill;", css.Replace("\r\n", "\n"));
    }

    /// <summary>An image brush mirrors in a right-to-left layout: every picture is made in the one place where it does not.</summary>
    [Fact]
    public void PicturesAreMadeInOnePlace()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AlUmmahNow.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var made = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "AlUmmahNow.Studio.Windows"), "*.cs")
            .SelectMany(File.ReadAllLines).Where(l => l.Contains("new ImageBrush", StringComparison.Ordinal) || l.Contains(".ImageBrush {", StringComparison.Ordinal));
        Assert.Equal("Background = new ImageBrush { ImageSource = source, Stretch = Stretch.UniformToFill },", Assert.Single(made).Trim());
    }

    /// <summary>A Studio brush that high contrast does not have is transparent there, or keeps the theme's color.</summary>
    [Fact]
    public void EveryStudioBrushHasARoleInHighContrast()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AlUmmahNow.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var glass = File.ReadAllText(Path.Combine(root!.FullName, "src", "AlUmmahNow.Studio.Windows", "LanternGlass.cs"));
        var names = System.Text.RegularExpressions.Regex.Matches(glass, "t\\[\"(Studio\\w+Brush)\"\\] = ").Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(names, HighContrast.Roles.Keys.OrderBy(n => n, StringComparer.Ordinal));
        // Applied, at the start and as the system's colors change
        Assert.Contains("FollowHighContrast(app, app.RequestedTheme == ApplicationTheme.Dark);", glass);
        Assert.Contains("        Paint();\n        Settings.ColorValuesChanged += (_, _) => ui.TryEnqueue(Paint);", glass.Replace("\r\n", "\n"));
        // Text is never the color of what it is on
        foreach (var (on, text) in new[] { ("StudioCardBrush", "StudioMutedBrush"), ("StudioCardBrush", "StudioLinkBrush"), ("StudioMenuBrush", "StudioDangerBrush"), ("StudioDangerFillBrush", "StudioOnDangerFillBrush"), ("StudioSidebarBrush", "StudioSelectedTextBrush"), ("StudioPickedCardBrush", "StudioTextBrush"), ("StudioPickedCardBrush", "StudioMutedBrush"), ("StudioPickedCardBrush", "StudioPickedEdgeBrush") })
        {
            Assert.NotEqual(HighContrast.Roles[on], HighContrast.Roles[text]);
            Assert.NotEqual(HighContrastRole.None, HighContrast.Roles[text]);
        }
    }

    [Fact]
    public void LanternStringIsAPng()
    {
        using var art = StudioTheme.LanternString();
        var head = new byte[8];
        Assert.Equal(8, art.Read(head, 0, 8));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, head);
    }
}
