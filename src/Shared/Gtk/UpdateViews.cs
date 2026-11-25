using System.ComponentModel;
using System.Runtime.InteropServices;
using AlUmmahNow.App.Demo;
using AlUmmahNow.App.Updates;

namespace AlUmmahNow.Updates.Views;

/// <summary>
/// The update bar, the screen of an unsupported version and Settings → Updates on GTK, for the viewer and Studio (the file
/// is compiled into both). Wiki AUN-APP-UPDATES §12.
/// </summary>
public sealed class UpdateViews : IUpdateViews, IDisposable
{
    // The viewer builds its window anew on each page and language: a notice is announced once all the same.
    private static string? _announced;
    private static bool _styled;

    private readonly UpdateService _service;
    private readonly UpdateText _text;
    private readonly Adw.ApplicationWindow _window;
    private readonly Gtk.Label _barText = Text("");
    private readonly Gtk.Button _barNotes;
    private readonly Gtk.Button _barDownload;
    private readonly Gtk.Button _close;
    private readonly Gtk.Label _forcedBody = Text("", center: true);
    private readonly Gtk.Label _forcedMessage = Text("", center: true);
    private readonly Gtk.Button _forcedDownload;
    private readonly Gtk.Label _version = Text("");
    private readonly Gtk.Label _lastChecked = Text("", dim: true);
    private readonly Gtk.Label _result = Text("");
    private readonly Gtk.Box _sectionHow;
    private readonly Gtk.Button _sectionNotes;
    private readonly Gtk.Button _sectionDownload;
    private readonly Gtk.Button _checkNow;
    private Gtk.Widget? _section;
    private string _openNotes = "";

    /// <param name="icon">The app's icon (PNG).</param>
    public UpdateViews(UpdateService service, UpdateText text, Adw.ApplicationWindow window, Stream icon, Action quit)
    {
        _service = service;
        _text = text;
        _window = window;
        Style();

        // The bar
        var info = Gtk.Image.NewFromIconName("help-about-symbolic");
        info.AddCssClass("update-info");
        info.SetValign(Gtk.Align.Center);
        _barText.SetHexpand(true);
        _barText.SetValign(Gtk.Align.Center);
        _barNotes = Button(text["updates.whatsNew"], ShowNotes, "flat");
        _barDownload = Button(text["updates.download"], service.Begin, "suggested-action");
        _close = Gtk.Button.NewFromIconName("window-close-symbolic");
        _close.AddCssClass("flat");
        _close.AddCssClass("circular");
        _close.SetValign(Gtk.Align.Center);
        _close.SetTooltipText(text["updates.close"]);
        Say(_close, text["updates.close"]);
        _close.OnClicked += (_, _) => service.Dismiss();
        var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        bar.AddCssClass("update-bar");
        foreach (var w in new Gtk.Widget[] { info, _barText, _barNotes, _barDownload, _close }) bar.Append(w);
        Bar = bar;

        // The screen
        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        body.AddCssClass("update-card");
        body.SetHalign(Gtk.Align.Center);
        body.SetValign(Gtk.Align.Center);
        body.SetVexpand(true);
        body.SetSizeRequest(500, -1);
        using var bytes = new MemoryStream();
        using (icon) icon.CopyTo(bytes);
        var image = Gtk.Image.NewFromPaintable(Gdk.Texture.NewFromBytes(GLib.Bytes.New(bytes.ToArray())));
        image.SetPixelSize(96);
        image.SetHalign(Gtk.Align.Center);
        image.AddCssClass("update-icon");
        image.SetOverflow(Gtk.Overflow.Hidden);
        body.Append(image);
        var title = Text(text["updates.forced.title"], center: true);
        title.AddCssClass("title-2");
        body.Append(title);
        body.Append(_forcedBody);
        body.Append(_forcedMessage);
        body.Append(How(center: true));
        var page = Text(text.Page, dim: true, center: true);
        page.SetSelectable(true);
        page.SetCanFocus(false);
        body.Append(page);
        _forcedDownload = Button(text["updates.download"], service.Begin, "suggested-action");
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        buttons.SetHalign(Gtk.Align.Center);
        buttons.SetMarginTop(8);
        buttons.Append(Button(text["updates.quit"], quit));
        buttons.Append(_forcedDownload);
        body.Append(buttons);
        var stage = Gtk.ScrolledWindow.New();
        stage.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        stage.SetVexpand(true);
        stage.AddCssClass("update-forced");
        stage.SetChild(body);
        Forced = stage;

        // Settings → Updates
        SectionBody = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        var heading = Text(text["updates.title"]);
        heading.AddCssClass("title-4");
        // The same height in both scripts, as the other cards' headings
        heading.SetSizeRequest(-1, 30);
        SectionBody.Append(heading);
        SectionBody.Append(_version);
        SectionBody.Append(_lastChecked);
        SectionBody.Append(_result);
        _sectionHow = How(center: false);
        SectionBody.Append(_sectionHow);
        _sectionNotes = Button(text["updates.whatsNew"], ShowNotes, "flat");
        _sectionDownload = Button(text["updates.download"], service.Begin, "suggested-action");
        _checkNow = Button(text["updates.checkNow"], () => _ = service.CheckAsync(isAsked: true));
        SectionActions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        foreach (var b in new[] { _sectionNotes, _sectionDownload, _checkNow }) SectionActions.Append(b);

        service.PropertyChanged += OnChanged;
        Render();
    }

    /// <summary>Under the header, across the window.</summary>
    public Gtk.Widget Bar { get; }

    /// <summary>In place of everything under the header; the window shows it when <see cref="IsForced"/>.</summary>
    public Gtk.Widget Forced { get; }

    public Gtk.Box SectionBody { get; }
    public Gtk.Box SectionActions { get; }

    /// <summary>The card the two are in: it is there only while Settings → Updates is.</summary>
    public void UseSection(Gtk.Widget card)
    {
        _section = card;
        Render();
    }

    public bool IsForced => _service.View.Stage == UpdateStage.Forced;

    /// <summary>The screen came or went.</summary>
    public event Action? ForcedChanged;

    public void Dispose() => _service.PropertyChanged -= OnChanged;

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        var was = Forced.GetVisible();
        Render();
        if (Forced.GetVisible() != was) ForcedChanged?.Invoke();
    }

    private void Render()
    {
        var v = _service.View;
        // What is new in a version that is there to update to
        var notes = v.CanDownload && _text.Notes(v).Length > 0;
        Bar.SetVisible(v.IsBar);
        _barText.SetText(_text.Bar(v));
        _barNotes.SetVisible(notes);
        _barDownload.SetVisible(v.CanDownload);
        _close.SetVisible(v.CanClose);

        Forced.SetVisible(IsForced);
        _forcedBody.SetText(_text.ForcedBody(v));
        _forcedMessage.SetText(_text.Message(v));
        _forcedMessage.SetVisible(_text.Message(v).Length > 0);

        var found = _service.Result == UpdateResult.Found;
        _section?.SetVisible(_service.IsSectionVisible);
        _version.SetText(_text.Version);
        _lastChecked.SetText(_text.LastChecked);
        var result = _text.Result;
        var told = _result.GetText();
        _result.SetText(result);
        _result.SetVisible(result.Length > 0);
        _sectionHow.SetVisible(found);
        _sectionDownload.SetVisible(found);
        _sectionNotes.SetVisible(found && _text.Notes(_service.Found).Length > 0);
        _checkNow.SetSensitive(_service.Result != UpdateResult.Checking);

        if (v.IsBar) Announce(v.Stage + ":" + v.Version, _text.Bar(v));
        else if (IsForced) Announce("forced", _text["updates.forced.title"] + " " + _text.ForcedBody(v));
        // What Check now came to, for a person who cannot see it change
        if (_service.IsCheckAsked && result != told && result.Length > 0) Speak(result);
    }

    /// <summary>Focus on the screen's button, when the window shows it.</summary>
    public void FocusForced() => _forcedDownload.GrabFocus();

    private void Announce(string key, string text)
    {
        if (_announced == key) return;
        _announced = key;
        Speak(text);
    }

    private void Speak(string text)
    {
        // Once the window is there: before it, no screen reader listens
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_LOW, () =>
        {
            if (IsHeard()) _window.Announce(text, Gtk.AccessibleAnnouncementPriority.Medium);
            return false;
        });
    }

    /// <summary>
    /// Whether the window's accessibility context is AT-SPI's. Without an accessibility bus GTK gives a stand-in, and GTK 4.14
    /// announces into it all the same: the app was gone with a segmentation fault (CI Linux runs its update scenes so).
    /// </summary>
    private bool IsHeard()
    {
        var context = gtk_accessible_get_at_context(_window.Handle.DangerousGetHandle());
        if (context == IntPtr.Zero) return false;
        var isHeard = Marshal.PtrToStringUTF8(g_type_name_from_instance(context)) == "GtkAtSpiContext";
        g_object_unref(context);
        return isHeard;
    }

    [DllImport("libgtk-4.so.1")]
    private static extern IntPtr gtk_accessible_get_at_context(IntPtr accessible);

    [DllImport("libgobject-2.0.so.0")]
    private static extern IntPtr g_type_name_from_instance(IntPtr instance);

    [DllImport("libgobject-2.0.so.0")]
    private static extern void g_object_unref(IntPtr gobject);

    private void ShowNotes()
    {
        var found = _service.Found;
        var notes = _text.Notes(found);
        if (notes.Length == 0) return;
        var dialog = Adw.AlertDialog.New(_text.NotesTitle(found), null);
        dialog.AddResponse("close", _text["common.close"]);
        dialog.SetDefaultResponse("close");
        dialog.SetCloseResponse("close");
        var label = Text(notes);
        label.SetHalign(Gtk.Align.Fill);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetPropagateNaturalHeight(true);
        scroll.SetMaxContentHeight(320);
        // Wide enough to read: the dialog is as narrow as it may be otherwise
        scroll.SetSizeRequest(420, -1);
        scroll.SetChild(label);
        dialog.SetExtraChild(scroll);
        dialog.OnClosed += (_, _) => _openNotes = "";
        _openNotes = notes;
        dialog.Present(_window);
    }

    private Gtk.Box How(bool center)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        box.Append(Text(_text.How, dim: true, center: center));
        if (_text.Command.Length > 0)
        {
            var command = Text(_text.Command, center: center);
            // A command is typed left to right in every language
            command.SetDirection(Gtk.TextDirection.Ltr);
            // And stands where the page's lines begin: its own start is the left
            if (!center) command.SetHalign(_text.IsRtl ? Gtk.Align.End : Gtk.Align.Start);
            command.SetSelectable(true);
            command.SetCanFocus(false);
            command.SetWrapMode(Pango.WrapMode.WordChar);
            command.AddCssClass("update-command");
            box.Append(command);
        }

        return box;
    }

    private static Gtk.Label Text(string text, bool dim = false, bool center = false)
    {
        // Set as text, never as markup: some of it comes from the server
        var label = Gtk.Label.New(null);
        label.SetText(text);
        label.SetWrap(true);
        label.SetXalign(center ? 0.5f : 0);
        label.SetHalign(center ? Gtk.Align.Center : Gtk.Align.Start);
        if (center) label.SetJustify(Gtk.Justification.Center);
        if (dim) label.AddCssClass("update-dim");
        return label;
    }

    private static Gtk.Button Button(string text, Action click, string? css = null)
    {
        var button = Gtk.Button.NewWithLabel(text);
        button.SetValign(Gtk.Align.Center);
        if (css != null) button.AddCssClass(css);
        button.OnClicked += (_, _) => click();
        return button;
    }

    private static void Say(Gtk.Widget widget, string name)
    {
        var values = new[] { new GObject.Value(name) };
        using var handle = GObject.Internal.ValueArray2OwnedHandle.Create(values);
        Gtk.Internal.Accessible.UpdateProperty(widget.Handle.DangerousGetHandle(), 1, new[] { Gtk.AccessibleProperty.Label }, handle);
        GC.KeepAlive(values);
    }

    /// <summary>From the theme's colors, so it follows Light and Dark in both apps.</summary>
    private const string Css = """
        .update-bar {
          background-color: @card_bg_color;
          background-image: linear-gradient(to bottom, alpha(@fanous_primary, 0.16), alpha(@fanous_primary, 0.06));
          border-bottom: 1px solid alpha(@fanous_primary_edge, 0.45);
          color: @fanous_text_primary;
          padding: 8px 16px;
        }
        .update-info { color: @fanous_link; }
        .update-bar button.flat { color: @fanous_link; }
        .update-dim { color: @fanous_text_secondary; }
        .update-forced {
          background-color: @fanous_background;
          background-image: radial-gradient(circle at 50% 42%, alpha(@fanous_primary, 0.14), transparent 55%);
          color: @fanous_text_primary;
        }
        .update-card {
          background-color: @card_bg_color;
          background-image: linear-gradient(to bottom, alpha(@fanous_primary_sheen, 0.10), transparent 35%);
          border: 1px solid alpha(@fanous_primary_edge, 0.45);
          border-radius: 14px;
          box-shadow: 0 12px 32px -16px alpha(@fanous_primary, 0.55), 0 1px 3px alpha(black, 0.10);
          color: @fanous_text_primary;
          padding: 28px;
          margin: 24px;
        }
        .update-icon { border-radius: 20px; }
        .update-card.update-settings { padding: 18px; margin: 0; }
        .update-command {
          font-family: monospace;
          background-color: alpha(@fanous_text_primary, 0.08);
          border-radius: 8px;
          padding: 6px 10px;
        }
        """;

    private static void Style()
    {
        if (_styled) return;
        _styled = true;
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(Css);
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, provider, 604);
    }

    // CI's scenes
    public bool IsBarShown => Bar.GetMapped() && Bar.GetHeight() > 0;
    public string BarText => _barText.GetText();
    public bool IsCloseShown => _close.GetMapped();
    public bool IsDownloadShown => _barDownload.GetMapped();
    public bool IsNotesShown => _barNotes.GetMapped();
    public string OpenNotes => _openNotes;
    public bool IsForcedShown => Forced.GetMapped() && Forced.GetHeight() > 0;
    public string ForcedText => _forcedBody.GetText() + "\n" + _forcedMessage.GetText();

    public bool IsForcedWhole
    {
        get
        {
            // Nothing else is in the window but the header
            var header = 0;
            for (var w = Forced.GetParent()?.GetFirstChild(); w != null; w = w.GetNextSibling())
            {
                if (w == Forced || !w.GetMapped()) continue;
                if (w is not Adw.HeaderBar) return false;
                header = w.GetHeight();
            }

            return Forced.GetWidth() == _window.GetWidth() && Math.Abs(Forced.GetHeight() + header - _window.GetHeight()) <= 1;
        }
    }

    public bool IsForcedFocused => _forcedDownload.HasFocus;

    public bool IsSectionShown => _section != null && SectionBody.GetMapped();
    public string SectionText => string.Join("\n", _version.GetText(), _lastChecked.GetText(), _result.GetText());
    public void PressClose() => _close.Activate();
    public void PressNotes() => _barNotes.Activate();

    public void PressDownload()
    {
        if (IsForcedShown) _forcedDownload.Activate();
        else _barDownload.Activate();
    }

    public void PressCheckNow() => _checkNow.Activate();
}
