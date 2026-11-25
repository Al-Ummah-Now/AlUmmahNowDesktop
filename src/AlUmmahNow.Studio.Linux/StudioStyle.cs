using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.Studio.Shell;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// Lantern Glass on GTK 4: the Fawanees tokens from the shared native theme registry (the light or
/// dark stylesheet, swapped with libadwaita's colour scheme), then the Studio's own styles
/// (<c>glass.css</c>): the sidebar, gold-edged cards,
/// menus and primary buttons. GTK can't blur behind a surface, so surfaces are solid with the
/// gold edge, sheen and glow.
/// </summary>
public static class StudioStyle
{
    private static readonly Logger Logger = Log.For("ui");
    private static Gtk.CssProvider? _tokens;
    private static Gtk.CssProvider? _header;

    public static void Apply()
    {
        var display = Gdk.Display.GetDefault()!;
        var manager = Adw.StyleManager.GetDefault();
        _tokens = Gtk.CssProvider.New();
        _header = Gtk.CssProvider.New();
        LoadTokens(manager.GetDark());
        Gtk.StyleContext.AddProviderForDisplay(display, _tokens, 600);
        manager.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "dark") LoadTokens(manager.GetDark());
        };

        var studio = Gtk.CssProvider.New();
        // The owner's lantern string hangs over the sign-in stage (the header itself has no art, as on the web).
        studio.LoadFromString(StudioTheme.Studio("glass.css").Replace("@LANTERN@", LanternFile()));
        Gtk.StyleContext.AddProviderForDisplay(display, studio, 601);
        Gtk.StyleContext.AddProviderForDisplay(display, _header, 602);

        // Without a compositor (X11, no compositing manager) a menu's glow can't be translucent: GTK paints
        // its whole margin as a solid block. Menus there keep their gold edge and drop the outer glow; they are square,
        // as their window is, whose color and gray border were seen around round corners.
        if (!display.IsComposited())
        {
            var flat = Gtk.CssProvider.New();
            flat.LoadFromString("popover.glass > contents, popover.glass > arrow { box-shadow: none; } popover > contents { box-shadow: none; } popover.glass { border: none; } popover.glass > contents { border-radius: 0; }");
            Gtk.StyleContext.AddProviderForDisplay(display, flat, 603);
        }
    }

    private static void LoadTokens(bool dark)
    {
        // The native registry's stylesheet: Fawanees colours and materials (a solid page since AlUmmahNow#1596).
        _tokens?.LoadFromString(StudioTheme.NativeGtk(dark));
        _header?.LoadFromString(HeaderCss(FawaneesHeader.For(dark)));
    }

    /// <summary>GTK CSS reads images from files: the lantern string is written once to the cache.</summary>
    private static string LanternFile()
    {
        try
        {
            var dir = Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "alummahnow-studio");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "lantern-string.png");
            using (var source = StudioTheme.LanternString())
            using (var file = File.Create(path))
            {
                source.CopyTo(file);
            }

            return new Uri(path).AbsoluteUri;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // No art: the stage keeps its glow.
            Logger.Warn("lantern art not written", ex);
            return "";
        }
    }

    /// <summary>
    /// The header bar, colour for colour the web Fawanees header (<see cref="FawaneesHeader"/>): the 90°
    /// gradient (no art, no glow, as on the web), the nav-border line over the gold rule at the bottom,
    /// nav-text and nav-icon, nav-hover on hover, nav-active-* when pressed.
    /// </summary>
    public static string HeaderCss(FawaneesHeader h)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var h40 = FawaneesHeader.SearchHeight.ToString(inv);
        var radius = h.SearchRadius.ToString(inv);
        var font = FawaneesHeader.SearchFontSize.ToString(inv);
        var inset = FawaneesHeader.SearchTextInset.ToString(inv);
        var button = FawaneesHeader.SearchButtonWidth.ToString(inv);
        return $@"
headerbar.night {{
  background-color: {h.Background.Css};
  /* Top layer: the bottom edge, as the web shows it, the gold rule (1 px) under the nav-border line (1 px).
     Painted in the background: libadwaita's header bar drew neither a bottom border nor two stacked shadows. */
  background-image: linear-gradient(to top, {h.GoldRule.Css} 0, {h.GoldRule.Css} 1px, {h.Border.Css} 1px, {h.Border.Css} 2px, transparent 2px),
    linear-gradient(to right, {h.Background.Css}, {h.Middle.Css} 50%, {h.Background.Css});
  color: {h.Text.Css};
  border: none;
  box-shadow: none;
  min-height: 48px;
}}
headerbar.night label, headerbar.night .title {{ color: {h.Text.Css}; }}
headerbar.night .subtitle, headerbar.night .dim-label {{ color: {h.Icon.Css}; opacity: 1; }}
headerbar.night button, headerbar.night windowcontrols button {{ color: {h.Icon.Css}; background-color: transparent; background-image: none; box-shadow: none; }}
headerbar.night button:hover, headerbar.night windowcontrols button:hover {{ color: {h.Text.Css}; background-color: {h.Hover.Css}; }}
headerbar.night button:active, headerbar.night button:checked, headerbar.night windowcontrols button:active {{ color: {h.ActiveText.Css}; background-color: {h.ActiveBackground.Css}; }}
headerbar.night .header-search {{
  min-height: {h40}px;
  border-radius: {radius}px;
  background-color: {h.SearchBackground.Css};
  box-shadow: inset 0 0 0 1px {h.SearchBorder.Css};
}}
headerbar.night .header-search:focus-within {{ box-shadow: inset 0 0 0 2px {h.SearchFocus.Css}; }}
headerbar.night .header-search entry {{
  min-height: {h40}px;
  background: none;
  border: none;
  box-shadow: none;
  outline: none;
  color: {h.SearchText.Css};
  font-size: {font}px;
  padding: 0 8px 0 {inset}px;
}}
headerbar.night .header-search entry:dir(rtl) {{ padding: 0 {inset}px 0 8px; }}
headerbar.night .header-search entry > text > placeholder {{ color: {h.SearchPlaceholder.Css}; opacity: 1; }}
headerbar.night .header-search button {{
  min-width: {button}px;
  min-height: {h40}px;
  padding: 0;
  border-radius: 0;
  color: {h.Icon.Css};
  background-color: transparent;
  box-shadow: inset 1px 0 {h.SearchBorder.Css};
}}
headerbar.night .header-search button:dir(rtl) {{ box-shadow: inset -1px 0 {h.SearchBorder.Css}; }}
headerbar.night .header-search button:hover {{ background-color: {h.Hover.Css}; color: {h.Icon.Css}; }}
";
    }
}
