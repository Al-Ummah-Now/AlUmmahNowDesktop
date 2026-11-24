using AlUmmahNow.App.I18n;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// Content under a row that opens it. Our own, not Gtk.Expander: the theme draws the expander's closed arrow from an icon
/// that falls back to a right-pointing one on Arabic pages. Closed, this one points along the reading direction.
/// </summary>
public sealed class Disclosure
{
    private readonly Gtk.Label _label;

    public Disclosure(Localizer l, string title, Gtk.Widget content)
    {
        var closedIcon = l.IsRtl ? "pan-start-symbolic" : "pan-end-symbolic";
        var arrow = Gtk.Image.NewFromIconName(closedIcon);
        // The icon has its own direction, which would turn the arrow once more: it is drawn as named
        arrow.SetDirection(Gtk.TextDirection.Ltr);
        _label = Gtk.Label.New("");
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        row.Append(arrow);
        row.Append(_label);
        Toggle.SetChild(row);
        Toggle.AddCssClass("flat");
        Toggle.SetHalign(Gtk.Align.Start);
        var reveal = Gtk.Revealer.New();
        reveal.SetTransitionType(Gtk.RevealerTransitionType.SlideDown);
        reveal.SetChild(content);
        Toggle.OnToggled += (_, _) =>
        {
            var open = Toggle.GetActive();
            arrow.SetFromIconName(open ? "pan-down-symbolic" : closedIcon);
            reveal.SetRevealChild(open);
            SayExpanded(open);
        };
        SetTitle(title);
        SayExpanded(false);
        Root.Append(Toggle);
        Root.Append(reveal);
    }

    public Gtk.Box Root { get; } = Gtk.Box.New(Gtk.Orientation.Vertical, 10);

    private Gtk.ToggleButton Toggle { get; } = Gtk.ToggleButton.New();

    public void SetTitle(string title)
    {
        _label.SetText(title);
        // A button whose child is a box takes no name from a label in it
        var values = new[] { new GObject.Value(title) };
        using var handle = GObject.Internal.ValueArray2OwnedHandle.Create(values);
        Gtk.Internal.Accessible.UpdateProperty(Toggle.Handle.DangerousGetHandle(), 1, new[] { Gtk.AccessibleProperty.Label }, handle);
        GC.KeepAlive(values);
    }

    /// <summary>Collapsed or expanded, as a screen reader says it (GTK keeps the state as a number: it may be undefined).</summary>
    private void SayExpanded(bool open)
    {
        var values = new[] { new GObject.Value(open ? 1 : 0) };
        using var handle = GObject.Internal.ValueArray2OwnedHandle.Create(values);
        Gtk.Internal.Accessible.UpdateState(Toggle.Handle.DangerousGetHandle(), 1, new[] { Gtk.AccessibleState.Expanded }, handle);
        GC.KeepAlive(values);
    }
}
