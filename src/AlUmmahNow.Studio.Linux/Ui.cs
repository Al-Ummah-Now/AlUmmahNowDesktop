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

    private static readonly HttpClient ThumbHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The bytes of a thumbnail: a local file (the demo's title cards) from disk, else over HTTP.</summary>
    public static Task<byte[]> ThumbnailBytesAsync(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile ? File.ReadAllBytesAsync(uri.LocalPath) : ThumbHttp.GetByteArrayAsync(url);

    /// <summary>A small thumbnail: the night tile until the image loads (or when there is none).</summary>
    public static Gtk.Widget Thumbnail(string? url, int width, int height)
    {
        var frame = Gtk.Overlay.New();
        frame.SetSizeRequest(width, height);
        frame.SetValign(Gtk.Align.Center);
        frame.SetOverflow(Gtk.Overflow.Hidden);
        frame.AddCssClass("file-tile");
        frame.SetChild(FileTile(width, height));
        if (url is { Length: > 0 }) _ = LoadAsync();
        return frame;

        async Task LoadAsync()
        {
            try
            {
                var data = await ThumbnailBytesAsync(url);
                var picture = Gtk.Picture.NewForPaintable(Gdk.Texture.NewFromBytes(GLib.Bytes.New(data)));
                picture.SetContentFit(Gtk.ContentFit.Cover);
                picture.SetCanShrink(true);
                picture.SetSizeRequest(width, height);
                frame.SetChild(picture);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or GLib.GException)
            {
                // The night tile stays.
            }
        }
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

    /// <summary>The same, with the label above the control (narrow cards).</summary>
    public static Gtk.Box LabelledAbove(string text, Gtk.Widget control)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        var label = Gtk.Label.NewWithMnemonic(text);
        label.SetMnemonicWidget(control);
        label.SetXalign(0);
        label.SetWrap(true);
        box.Append(label);
        box.Append(control);
        return box;
    }

    /// <summary>Labels a control for screen readers through a visible label (AT-SPI labelled-by).</summary>
    /// <summary>
    /// A dialog form: one label column and one control column, so every field starts at the same
    /// offset and fills the same width (as the Windows dialogs). Add rows with <see cref="FormRow"/>.
    /// </summary>
    public static Gtk.Grid Form()
    {
        var grid = Gtk.Grid.New();
        grid.SetColumnSpacing(12);
        grid.SetRowSpacing(8);
        grid.SetSizeRequest(460, -1);
        return grid;
    }

    /// <summary>A labelled row of a <see cref="Form"/>: the label at the start, the control filling the rest.</summary>
    public static void FormRow(Gtk.Grid form, string text, Gtk.Widget control, bool top = false)
    {
        var row = 0;
        for (var child = form.GetFirstChild(); child != null; child = child.GetNextSibling()) row++;
        row /= 2;
        var label = Gtk.Label.NewWithMnemonic(text);
        label.SetMnemonicWidget(control);
        label.SetHalign(Gtk.Align.Start);
        label.SetXalign(0);
        // A long label ("Tags (comma-separated)") wraps rather than widening the label column.
        label.SetWrap(true);
        label.SetMaxWidthChars(16);
        label.SetValign(top ? Gtk.Align.Start : Gtk.Align.Center);
        if (top) label.SetMarginTop(6);
        control.SetHexpand(true);
        control.SetHalign(Gtk.Align.Fill);
        form.Attach(label, 0, row, 1, 1);
        form.Attach(control, 1, row, 1, 1);
    }

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
