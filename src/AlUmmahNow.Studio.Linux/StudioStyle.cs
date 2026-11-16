namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// Surfaces: menus (popovers) and cards are frosted glass (translucent neutral fill, thin bright
/// edge, soft golden glow); everything else stays flat libadwaita. GTK can't blur what is behind a
/// surface itself: with a compositor that blurs translucent windows (KWin, Mutter's blur extensions,
/// picom) the menus are blurred; without one they are translucent.
/// </summary>
public static class StudioStyle
{
    private const string Css = @"
popover.glass > contents {
  background-color: alpha(@popover_bg_color, 0.9);
  border: 1px solid alpha(white, 0.55);
  box-shadow: 0 0 18px 2px alpha(#d9b25f, 0.38), 0 6px 18px alpha(black, 0.22);
}
popover.glass > arrow {
  background-color: alpha(@popover_bg_color, 0.9);
  border: 1px solid alpha(white, 0.55);
}
.card {
  background-color: alpha(@card_bg_color, 0.9);
  border: 1px solid alpha(white, 0.45);
  box-shadow: 0 0 14px 1px alpha(#d9b25f, 0.30), 0 2px 6px alpha(black, 0.12);
}
";

    public static void Apply()
    {
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(Css);
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, provider, 600);
    }
}
