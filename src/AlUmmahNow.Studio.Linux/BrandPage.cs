using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Institution;

namespace AlUmmahNow.Studio.Linux;

/// <summary>Institution mode: every channel, the brand kit editor with a preview, and the shared asset library.</summary>
public sealed class BrandPage
{
    private static readonly LowerThirdStyle[] Styles = { LowerThirdStyle.Bar, LowerThirdStyle.Box, LowerThirdStyle.Minimal };
    private static readonly LowerThirdPosition[] Positions = { LowerThirdPosition.Start, LowerThirdPosition.Center, LowerThirdPosition.End };
    private static readonly WatermarkCorner[] Corners = { WatermarkCorner.None, WatermarkCorner.TopStart, WatermarkCorner.TopEnd, WatermarkCorner.BottomStart, WatermarkCorner.BottomEnd };

    private readonly Localizer _l;
    private readonly InstitutionViewModel _vm;
    private readonly Gtk.Window _window;
    private readonly Gtk.Box _channels = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.Label _status = Ui.Label("", dim: true);
    private readonly Gtk.Label _readOnly = Ui.Label("");
    private readonly Gtk.Label _error = Ui.Label("");
    private readonly Gtk.Label _notice = Ui.Label("");
    private readonly Gtk.Entry _nameEn = Gtk.Entry.New();
    private readonly Gtk.Entry _nameAr = Gtk.Entry.New();
    private readonly Gtk.Entry[] _colors = { Gtk.Entry.New(), Gtk.Entry.New(), Gtk.Entry.New(), Gtk.Entry.New() };
    private readonly Gtk.DropDown _logo = Gtk.DropDown.NewFromStrings(Array.Empty<string>());
    private readonly Gtk.DropDown _intro = Gtk.DropDown.NewFromStrings(Array.Empty<string>());
    private readonly Gtk.DropDown _outro = Gtk.DropDown.NewFromStrings(Array.Empty<string>());
    private readonly Gtk.DropDown _style;
    private readonly Gtk.DropDown _position;
    private readonly Gtk.DropDown _watermark;
    private readonly Gtk.DrawingArea _preview = Gtk.DrawingArea.New();
    private readonly Gtk.Button _save;
    private readonly Gtk.Button _revert;
    private readonly Gtk.Box _assets = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Gtk.Label _usage = Ui.Label("", dim: true);
    private readonly Gtk.Button _add;
    private readonly List<Gtk.Widget> _editors = new();
    private IReadOnlyList<string?> _logoIds = Array.Empty<string?>();
    private IReadOnlyList<string?> _introIds = Array.Empty<string?>();
    private IReadOnlyList<string?> _outroIds = Array.Empty<string?>();
    private bool _syncing;

    public BrandPage(Localizer l, InstitutionViewModel vm, Gtk.Window window)
    {
        _l = l;
        _vm = vm;
        _window = window;
        _vm.Confirm = ConfirmKeys.For(window, l);
        Root = Gtk.ScrolledWindow.New();
        var page = Ui.Page(14);
        Root.SetChild(page);

        page.Append(Ui.Heading(l["studio.channels.title"]));
        page.Append(Ui.Label(l["studio.channels.body"], dim: true));
        page.Append(_channels);

        page.Append(Ui.Heading(l["studio.brand.title"]));
        page.Append(Ui.Label(l["studio.brand.body"], dim: true));
        page.Append(_status);
        _readOnly.AddCssClass("warning");
        page.Append(_readOnly);
        _error.AddCssClass("error");
        page.Append(_error);
        _notice.AddCssClass("success");
        page.Append(_notice);

        var form = Gtk.Grid.New();
        form.SetColumnSpacing(12);
        form.SetRowSpacing(8);
        var row = 0;
        void Field(string key, Gtk.Widget control)
        {
            var label = Gtk.Label.NewWithMnemonic(l[key]);
            label.SetHalign(Gtk.Align.Start);
            label.SetMnemonicWidget(control);
            form.Attach(label, 0, row, 1, 1);
            control.SetHexpand(true);
            form.Attach(control, 1, row, 1, 1);
            _editors.Add(control);
            row++;
        }

        _nameEn.SetDirection(Gtk.TextDirection.Ltr);
        _nameAr.SetDirection(Gtk.TextDirection.Rtl);
        _nameEn.SetMaxLength(BrandKit.MaxNameChars);
        _nameAr.SetMaxLength(BrandKit.MaxNameChars);
        _nameEn.OnChanged += (_, _) => Edit(k => k.NameEn = _nameEn.GetText());
        _nameAr.OnChanged += (_, _) => Edit(k => k.NameAr = _nameAr.GetText());
        Field("studio.brand.nameEn", _nameEn);
        Field("studio.brand.nameAr", _nameAr);
        string[] colorKeys = { "studio.brand.primary", "studio.brand.secondary", "studio.brand.text", "studio.brand.background" };
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            var entry = _colors[i];
            entry.SetDirection(Gtk.TextDirection.Ltr);
            entry.SetMaxLength(7);
            entry.SetWidthChars(9);
            entry.SetPlaceholderText("#rrggbb");
            entry.OnChanged += (_, _) => Edit(k =>
            {
                var v = entry.GetText().Trim().ToLowerInvariant();
                if (index == 0) k.Primary = v;
                else if (index == 1) k.Secondary = v;
                else if (index == 2) k.TextColor = v;
                else k.Background = v;
            });
            Field(colorKeys[i], entry);
        }

        Pick(_logo, "studio.brand.logo", Field, id => Edit(k => k.Logo = id), () => _logoIds);
        Pick(_intro, "studio.brand.intro", Field, id => Edit(k => k.Intro = id), () => _introIds);
        Pick(_outro, "studio.brand.outro", Field, id => Edit(k => k.Outro = id), () => _outroIds);
        _style = Gtk.DropDown.NewFromStrings(new[] { l["studio.brand.style.bar"], l["studio.brand.style.box"], l["studio.brand.style.minimal"] });
        _style.OnNotify += (_, e) => { if (!_syncing && e.Pspec.GetName() == "selected") Edit(k => k.LowerThirdStyle = Styles[(int)_style.GetSelected()]); };
        Field("studio.brand.lowerThird", _style);
        _position = Gtk.DropDown.NewFromStrings(new[] { l["studio.brand.position.start"], l["studio.brand.position.center"], l["studio.brand.position.end"] });
        _position.OnNotify += (_, e) => { if (!_syncing && e.Pspec.GetName() == "selected") Edit(k => k.LowerThirdPosition = Positions[(int)_position.GetSelected()]); };
        Field("studio.brand.lowerThirdPosition", _position);
        _watermark = Gtk.DropDown.NewFromStrings(Corners.Select(c => l["studio.brand.watermark." + BrandKit.Corner(c)]).ToArray());
        _watermark.OnNotify += (_, e) => { if (!_syncing && e.Pspec.GetName() == "selected") Edit(k => k.Watermark = Corners[(int)_watermark.GetSelected()]); };
        Field("studio.brand.watermark", _watermark);

        var split = Gtk.Box.New(Gtk.Orientation.Horizontal, 20);
        form.SetHexpand(true);
        split.Append(form);
        var previewBox = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        previewBox.Append(Ui.Label(l["studio.brand.preview"], dim: true));
        _preview.SetContentWidth(384);
        _preview.SetContentHeight(216);
        _preview.SetDrawFunc((_, cr, w, h) => DrawPreview(cr, w, h));
        _preview.SetTooltipText(l["studio.brand.previewName"]);
        previewBox.Append(_preview);
        split.Append(previewBox);
        page.Append(split);

        var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _save = Ui.Button(l["studio.brand.save"], () => _ = _vm.SaveAsync(), "suggested-action");
        _revert = Ui.Button(l["studio.brand.revert"], () => _vm.Revert(), "flat");
        actions.Append(_save);
        actions.Append(_revert);
        page.Append(actions);

        var assetsHead = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var ah = Ui.Heading(l["studio.assets.title"]);
        ah.SetHexpand(true);
        assetsHead.Append(ah);
        _add = Ui.Button(l["studio.assets.add"], () => _ = PickAssetAsync());
        assetsHead.Append(_add);
        page.Append(assetsHead);
        page.Append(Ui.Label(l["studio.assets.body"], dim: true));
        page.Append(_usage);
        page.Append(_assets);

        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public Gtk.ScrolledWindow Root { get; }

    private void Pick(Gtk.DropDown drop, string key, Action<string, Gtk.Widget> field, Action<string?> set, Func<IReadOnlyList<string?>> ids)
    {
        drop.OnNotify += (_, e) =>
        {
            if (_syncing || e.Pspec.GetName() != "selected") return;
            var list = ids();
            var i = (int)drop.GetSelected();
            if (i >= 0 && i < list.Count) set(list[i]);
        };
        field(key, drop);
    }

    private void Edit(Action<BrandKit> change)
    {
        if (_syncing) return;
        _vm.Edit(change);
    }

    private void Render()
    {
        Ui.Clear(_channels);
        foreach (var c in _vm.Channels)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            row.AddCssClass("card");
            row.AddCssClass("aun-pad");
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            text.SetHexpand(true);
            var name = Ui.Label(c.Name);
            name.AddCssClass("heading");
            text.Append(name);
            var role = _l["studio.channels.role." + c.Role.ToString().ToLowerInvariant()];
            text.Append(Ui.Label(c.Handle == null ? role : _l.Format("studio.channels.line", "@" + c.Handle, role), dim: true));
            row.Append(text);
            if (ReferenceEquals(c, _vm.Active) || c.Id == _vm.Active?.Id)
            {
                var active = Ui.Label(_l["studio.channels.active"], dim: true, start: false);
                active.SetWrap(false);
                active.SetValign(Gtk.Align.Center);
                row.Append(active);
            }
            else
            {
                var sw = Ui.Named(Ui.Button(_l["studio.channels.switch"], () => _vm.Switch(c)), _l.Format("studio.channels.switchTo", c.Name));
                sw.SetValign(Gtk.Align.Center);
                row.Append(sw);
            }

            _channels.Append(row);
        }

        Ui.Status(_status, _vm.IsLoading ? _l["studio.shell.loading"] : null);
        Ui.Status(_readOnly, _vm.State != null && !_vm.CanEdit ? _l["studio.brand.readOnly"] : null);
        Ui.Status(_error, _vm.ErrorKey is { } e ? _l[e] : null);
        Ui.Status(_notice, _vm.NoticeKey is { } n && !_vm.IsDirty ? _l[n] : null);

        var k = _vm.Kit;
        var lib = _vm.Library;
        _syncing = true;
        try
        {
            SetText(_nameEn, k.NameEn ?? "");
            SetText(_nameAr, k.NameAr ?? "");
            SetText(_colors[0], k.Primary);
            SetText(_colors[1], k.Secondary);
            SetText(_colors[2], k.TextColor);
            SetText(_colors[3], k.Background);
            _logoIds = Options(_logo, lib, StudioAssetKind.Image, k.Logo);
            _introIds = Options(_intro, lib, StudioAssetKind.Video, k.Intro);
            _outroIds = Options(_outro, lib, StudioAssetKind.Video, k.Outro);
            _style.SetSelected((uint)Array.IndexOf(Styles, k.LowerThirdStyle));
            _position.SetSelected((uint)Array.IndexOf(Positions, k.LowerThirdPosition));
            _watermark.SetSelected((uint)Array.IndexOf(Corners, k.Watermark));
        }
        finally
        {
            _syncing = false;
        }

        // Team members read the owner's kit: text fields stay readable at full contrast (read-only, not greyed out).
        foreach (var w in _editors)
        {
            if (w is Gtk.Entry entry) entry.SetEditable(_vm.CanEdit);
            else w.SetSensitive(_vm.CanEdit);
        }
        for (var i = 0; i < 4; i++)
        {
            if (BrandKit.IsValidColor(_colors[i].GetText())) _colors[i].RemoveCssClass("error");
            else _colors[i].AddCssClass("error");
        }

        _save.SetSensitive(_vm.CanEdit && _vm.IsDirty && !_vm.IsSaving);
        _revert.SetSensitive(_vm.CanEdit && _vm.IsDirty);
        _save.SetVisible(_vm.CanEdit);
        _revert.SetVisible(_vm.CanEdit);
        _add.SetVisible(_vm.CanEdit);
        _add.SetSensitive(!_vm.IsSaving);
        _preview.QueueDraw();

        Ui.Clear(_assets);
        if (lib != null)
        {
            _usage.SetText(_l.Format("studio.assets.usage", _l.Number(lib.Assets.Count), _l.Number(lib.MaxAssets), Size(lib.Bytes), Size(lib.MaxTotalBytes)));
            if (lib.Assets.Count == 0) _assets.Append(Ui.Label(_l["studio.assets.none"], dim: true));
            foreach (var a in lib.Assets)
            {
                var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
                var icon = Gtk.Image.NewFromIconName(a.Kind switch { StudioAssetKind.Video => "video-x-generic-symbolic", StudioAssetKind.Audio => "audio-x-generic-symbolic", _ => "image-x-generic-symbolic" });
                icon.SetTooltipText(_l["studio.assets.kind." + a.Kind.ToString().ToLowerInvariant()]);
                row.Append(icon);
                var name = Ui.Label(a.Name);
                name.SetHexpand(true);
                row.Append(name);
                row.Append(Ui.Label(_l["studio.assets.kind." + a.Kind.ToString().ToLowerInvariant()], dim: true));
                row.Append(Ui.Label(Size(a.Bytes), dim: true));
                if (_vm.CanEdit) row.Append(Ui.Named(Ui.Button(_l["studio.assets.delete"], () => _ = _vm.DeleteAssetAsync(a), "flat"), _l.Format("studio.assets.deleteName", a.Name)));
                _assets.Append(row);
            }
        }
    }

    private static void SetText(Gtk.Entry entry, string text)
    {
        if (entry.GetText() != text) entry.SetText(text);
    }

    private IReadOnlyList<string?> Options(Gtk.DropDown drop, StudioAssetLibrary? lib, StudioAssetKind kind, string? current)
    {
        var ids = new List<string?> { null };
        var names = new List<string> { _l["studio.brand.none"] };
        foreach (var a in lib?.OfKind(kind) ?? Enumerable.Empty<StudioAsset>())
        {
            ids.Add(a.Id);
            names.Add(a.Name);
        }

        drop.SetModel(Gtk.StringList.New(names.ToArray()));
        drop.SetSelected((uint)Math.Max(0, ids.IndexOf(current)));
        return ids;
    }

    private string Size(long bytes) => bytes >= 1_000_000_000 ? _l.Format("studio.size.gb", _l.Decimal(bytes / 1e9)) : _l.Format("studio.size.mb", _l.Decimal(Math.Round(bytes / 1e6, 1)));

    private static (double R, double G, double B) Rgb(string hex, string fallback)
    {
        var v = BrandKit.IsValidColor(hex) ? hex : fallback;
        return (Convert.ToInt32(v.Substring(1, 2), 16) / 255.0, Convert.ToInt32(v.Substring(3, 2), 16) / 255.0, Convert.ToInt32(v.Substring(5, 2), 16) / 255.0);
    }

    /// <summary>A 16:9 frame in the kit's colours with the lower third and the watermark corner, mirrored in Arabic.</summary>
    private void DrawPreview(Cairo.Context cr, int w, int h)
    {
        var k = _vm.Kit;
        var rtl = _l.IsRtl;
        var bg = Rgb(k.Background, BrandKit.DefaultBackground);
        var primary = Rgb(k.Primary, BrandKit.DefaultPrimary);
        var secondary = Rgb(k.Secondary, BrandKit.DefaultSecondary);
        var text = Rgb(k.TextColor, BrandKit.DefaultText);
        cr.SetSourceRgb(bg.R, bg.G, bg.B);
        cr.Rectangle(0, 0, w, h);
        cr.Fill();

        var name = k.DisplayName(_l.Language) ?? _l["studio.brand.sampleName"];
        var layout = _preview.CreatePangoLayout(name);
        layout.GetPixelSize(out var tw, out var th);
        var barW = k.LowerThirdStyle == LowerThirdStyle.Minimal ? tw + 16 : Math.Max(tw + 40, w * 0.55);
        var barH = th + 18;
        var x = k.LowerThirdPosition switch
        {
            LowerThirdPosition.Center => (w - barW) / 2,
            LowerThirdPosition.End => rtl ? 16 : w - barW - 16,
            _ => rtl ? w - barW - 16 : 16,
        };
        var y = h - barH - 20;
        if (k.LowerThirdStyle == LowerThirdStyle.Bar)
        {
            cr.SetSourceRgb(secondary.R, secondary.G, secondary.B);
            cr.Rectangle(x, y, barW, barH);
            cr.Fill();
            cr.SetSourceRgb(primary.R, primary.G, primary.B);
            cr.Rectangle(rtl ? x + barW - 6 : x, y, 6, barH);
            cr.Fill();
        }
        else if (k.LowerThirdStyle == LowerThirdStyle.Box)
        {
            cr.SetSourceRgb(primary.R, primary.G, primary.B);
            cr.Rectangle(x, y, barW, barH);
            cr.Fill();
        }
        else
        {
            cr.SetSourceRgb(primary.R, primary.G, primary.B);
            cr.Rectangle(x, y + barH - 3, barW, 3);
            cr.Fill();
        }

        cr.SetSourceRgb(text.R, text.G, text.B);
        var tx = k.LowerThirdPosition == LowerThirdPosition.Center ? x + ((barW - tw) / 2) : rtl ? x + barW - tw - 16 : x + 16;
        if (k.LowerThirdStyle == LowerThirdStyle.Minimal) tx = rtl ? x + barW - tw - 8 : x + 8;
        cr.MoveTo(tx, y + 9);
        PangoCairo.Functions.ShowLayout(cr, layout);

        if (k.Watermark != WatermarkCorner.None)
        {
            var size = 34.0;
            var top = k.Watermark is WatermarkCorner.TopStart or WatermarkCorner.TopEnd;
            var startSide = k.Watermark is WatermarkCorner.TopStart or WatermarkCorner.BottomStart;
            var left = startSide != rtl;
            var wx = left ? 12 : w - size - 12;
            var wy = top ? 12 : h - size - 12;
            if (!top && Math.Abs(wy - y) < barH + 4) wy = y - size - 8;
            cr.SetSourceRgba(primary.R, primary.G, primary.B, 0.85);
            cr.Arc(wx + (size / 2), wy + (size / 2), size / 2, 0, 2 * Math.PI);
            cr.Fill();
        }
    }

    private async Task PickAssetAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(_l["studio.assets.pick"]);
            var file = await dialog.OpenAsync(_window);
            if (file?.GetPath() is { } path) await _vm.AddAssetAsync(path);
        }
        catch (GLib.GException)
        {
            // dismissed
        }
    }
}
