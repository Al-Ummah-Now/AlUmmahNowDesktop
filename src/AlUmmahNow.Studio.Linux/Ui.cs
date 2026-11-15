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

    public static Gtk.Label Heading(string text)
    {
        var h = Label(text);
        // Headings stay on one line; next to a button they would otherwise wrap word by word.
        h.SetWrap(false);
        h.AddCssClass("heading");
        return h;
    }

    /// <summary>Sets a status line and hides it when empty, so empty lines leave no gap.</summary>
    public static void Status(Gtk.Label label, string? text)
    {
        label.SetText(text ?? "");
        label.SetVisible(!string.IsNullOrEmpty(text));
    }

    /// <summary>A padded card (Adwaita "card" plus the app's padding class).</summary>
    public static Gtk.Box Card(int spacing = 8)
    {
        var b = Gtk.Box.New(Gtk.Orientation.Vertical, spacing);
        b.AddCssClass("card");
        b.AddCssClass("aun-pad");
        return b;
    }
}
