namespace AlUmmahNow.Studio.Linux;

/// <summary>Small GTK helpers shared by the pages.</summary>
public static class Ui
{
    public static Gtk.Box Page(int spacing = 10)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, spacing);
        box.SetMarginStart(20);
        box.SetMarginEnd(20);
        box.SetMarginTop(16);
        box.SetMarginBottom(16);
        return box;
    }

    public static Gtk.Label Label(string text, bool dim = false, bool start = true)
    {
        var label = Gtk.Label.New(text);
        if (start) label.SetHalign(Gtk.Align.Start);
        label.SetWrap(true);
        label.SetXalign(0);
        if (dim) label.AddCssClass("dim-label");
        return label;
    }

    public static Gtk.Button Button(string text, Action onClick, string? css = null)
    {
        var b = Gtk.Button.NewWithLabel(text);
        if (css != null) b.AddCssClass(css);
        b.OnClicked += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// A round logo: our own initials (one grapheme per word, «قناة النور» → «ق») on gold for a channel or
    /// teal for a person until the picture loads, then the picture. Not Adw.Avatar: its own palette wins.
    /// </summary>
    public static Gtk.Widget Avatar(string? initials, Gdk.Texture? picture, int size, bool person)
    {
        var face = Gtk.Overlay.New();
        face.SetSizeRequest(size, size);
        face.SetHalign(Gtk.Align.Center);
        face.SetValign(Gtk.Align.Center);
        face.SetOverflow(Gtk.Overflow.Hidden);
        face.AddCssClass("studio-avatar");
        face.AddCssClass(person ? "person" : "channel");
        var label = Gtk.Label.New(initials ?? "");
        label.SetHalign(Gtk.Align.Center);
        label.SetValign(Gtk.Align.Center);
        label.SetSizeRequest(size, size);
        label.SetDirection(Gtk.TextDirection.Ltr);
        face.SetChild(label);
        if (picture != null)
        {
            var image = Gtk.Picture.NewForPaintable(picture);
            image.SetContentFit(Gtk.ContentFit.Cover);
            image.SetCanShrink(true);
            face.AddOverlay(image);
        }

        return face;
    }

    /// <summary>
    /// A Lantern Glass card: gold edge, top sheen, gold glow (glass.css). Buttons go in
    /// <paramref name="footer"/>, end-aligned under a hairline.
    /// </summary>
    public static Gtk.Box Card(Gtk.Widget body, Gtk.Widget? footer = null)
    {
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        card.AddCssClass("card");
        body.SetMarginStart(18);
        body.SetMarginEnd(18);
        body.SetMarginTop(14);
        body.SetMarginBottom(16);
        card.Append(body);
        if (footer != null)
        {
            var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            bar.AddCssClass("card-footer");
            footer.SetHalign(Gtk.Align.End);
            footer.SetHexpand(true);
            bar.Append(footer);
            card.Append(bar);
        }

        return card;
    }

    /// <summary>A heading inside a card.</summary>
    public static Gtk.Label CardTitle(string text)
    {
        var label = Label(text);
        label.AddCssClass("title-4");
        label.SetWrap(false);
        return label;
    }

    /// <summary>A video file's tile: night blue with a gold film glyph (the server makes the real thumbnail).</summary>
    public static Gtk.Widget FileTile(int width, int height)
    {
        var tile = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        tile.AddCssClass("file-tile");
        tile.SetSizeRequest(width, height);
        tile.SetValign(Gtk.Align.Center);
        var icon = Gtk.Image.NewFromIconName("video-x-generic-symbolic");
        icon.SetPixelSize(height * 2 / 5);
        icon.SetVexpand(true);
        icon.SetValign(Gtk.Align.Center);
        tile.Append(icon);
        return tile;
    }

    public static void Clear(Gtk.Box box)
    {
        while (box.GetFirstChild() is { } child) box.Remove(child);
    }

    /// <summary>Tooltip with the full action ("Edit <title>"); the control's own label is what screen readers announce.</summary>
    public static T Named<T>(T widget, string name) where T : Gtk.Widget
    {
        widget.SetTooltipText(name);
        return widget;
    }

    /// <summary>Labels a control for screen readers through a visible label (AT-SPI labelled-by).</summary>
    public static Gtk.Box Labelled(string text, Gtk.Widget control)
    {
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var label = Gtk.Label.NewWithMnemonic(text);
        label.SetMnemonicWidget(control);
        row.Append(label);
        row.Append(control);
        return row;
    }
}
