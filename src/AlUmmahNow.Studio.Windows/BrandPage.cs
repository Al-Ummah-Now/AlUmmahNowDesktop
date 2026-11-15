using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Institution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>Institution mode: every channel, the brand kit editor with a preview, and the shared asset library.</summary>
internal sealed class BrandPage
{
    private static readonly LowerThirdStyle[] Styles = { LowerThirdStyle.Bar, LowerThirdStyle.Box, LowerThirdStyle.Minimal };
    private static readonly LowerThirdPosition[] Positions = { LowerThirdPosition.Start, LowerThirdPosition.Center, LowerThirdPosition.End };
    private static readonly WatermarkCorner[] Corners = { WatermarkCorner.None, WatermarkCorner.TopStart, WatermarkCorner.TopEnd, WatermarkCorner.BottomStart, WatermarkCorner.BottomEnd };

    private readonly Localizer _l;
    private readonly InstitutionViewModel _vm;
    private readonly Func<Task<string?>> _pickAsset;
    private readonly StackPanel _channels = new() { Spacing = 8 };
    private readonly TextBlock _status = Ui.Text("", dim: true);
    private readonly InfoBar _readOnly = Ui.Bar(InfoBarSeverity.Warning);
    private readonly InfoBar _error = Ui.Bar(InfoBarSeverity.Error);
    private readonly InfoBar _notice = Ui.Bar(InfoBarSeverity.Success);
    private readonly TextBox _nameEn;
    private readonly TextBox _nameAr;
    private readonly TextBox[] _colors = new TextBox[4];
    private readonly ComboBox _logo;
    private readonly ComboBox _intro;
    private readonly ComboBox _outro;
    private readonly ComboBox _style;
    private readonly ComboBox _position;
    private readonly ComboBox _watermark;
    private readonly Canvas _preview = new() { Width = 384, Height = 216, FlowDirection = FlowDirection.LeftToRight };
    private readonly Button _save;
    private readonly Button _revert;
    private readonly StackPanel _assets = new() { Spacing = 4 };
    private readonly TextBlock _usage = Ui.Text("", dim: true);
    private readonly Button _add;
    private readonly List<Control> _editors = new();
    private IReadOnlyList<string?> _logoIds = Array.Empty<string?>();
    private IReadOnlyList<string?> _introIds = Array.Empty<string?>();
    private IReadOnlyList<string?> _outroIds = Array.Empty<string?>();
    private bool _syncing;

    public BrandPage(Localizer l, InstitutionViewModel vm, Func<XamlRoot?> root, Func<Task<string?>> pickAsset)
    {
        _l = l;
        _vm = vm;
        _pickAsset = pickAsset;
        _vm.Confirm = ConfirmKeys.For(root, l);
        Root = new StackPanel { Spacing = 12, Padding = new Thickness(20) };

        Root.Children.Add(Ui.Heading(l["studio.channels.title"]));
        Root.Children.Add(Ui.Text(l["studio.channels.body"], dim: true));
        Root.Children.Add(_channels);

        Root.Children.Add(Ui.Heading(l["studio.brand.title"]));
        Root.Children.Add(Ui.Text(l["studio.brand.body"], dim: true));
        Root.Children.Add(_status);
        Root.Children.Add(_readOnly);
        Root.Children.Add(_error);
        Root.Children.Add(_notice);

        var form = new StackPanel { Spacing = 10, MinWidth = 320 };
        T Field<T>(T control) where T : Control
        {
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            form.Children.Add(control);
            _editors.Add(control);
            return control;
        }

        string H(string key) => l[key].Replace("_", "", StringComparison.Ordinal);

        // Values read in their own direction; headers stay on the page's side.
        _nameEn = Field(new TextBox { Header = H("studio.brand.nameEn"), MaxLength = BrandKit.MaxNameChars, TextReadingOrder = TextReadingOrder.DetectFromContent, TextAlignment = TextAlignment.DetectFromContent });
        _nameAr = Field(new TextBox { Header = H("studio.brand.nameAr"), MaxLength = BrandKit.MaxNameChars, TextReadingOrder = TextReadingOrder.DetectFromContent, TextAlignment = TextAlignment.DetectFromContent });
        _nameEn.TextChanged += (_, _) => Edit(k => k.NameEn = _nameEn.Text);
        _nameAr.TextChanged += (_, _) => Edit(k => k.NameAr = _nameAr.Text);
        string[] colorKeys = { "studio.brand.primary", "studio.brand.secondary", "studio.brand.text", "studio.brand.background" };
        var colorRow = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < 4; i++)
        {
            colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var index = i;
            var box = new TextBox { Header = H(colorKeys[i]), MaxLength = 7, PlaceholderText = "#rrggbb", TextReadingOrder = TextReadingOrder.DetectFromContent, TextAlignment = TextAlignment.DetectFromContent };
            box.TextChanged += (_, _) => Edit(k =>
            {
                var v = box.Text.Trim().ToLowerInvariant();
                if (index == 0) k.Primary = v;
                else if (index == 1) k.Secondary = v;
                else if (index == 2) k.TextColor = v;
                else k.Background = v;
            });
            _colors[i] = box;
            _editors.Add(box);
            Grid.SetColumn(box, i);
            colorRow.Children.Add(box);
        }

        form.Children.Add(colorRow);
        _logo = Field(Pick(H("studio.brand.logo"), id => Edit(k => k.Logo = id), () => _logoIds));
        _intro = Field(Pick(H("studio.brand.intro"), id => Edit(k => k.Intro = id), () => _introIds));
        _outro = Field(Pick(H("studio.brand.outro"), id => Edit(k => k.Outro = id), () => _outroIds));
        _style = Field(new ComboBox { Header = H("studio.brand.lowerThird"), ItemsSource = new[] { l["studio.brand.style.bar"], l["studio.brand.style.box"], l["studio.brand.style.minimal"] } });
        _style.SelectionChanged += (_, _) => { if (!_syncing && _style.SelectedIndex >= 0) Edit(k => k.LowerThirdStyle = Styles[_style.SelectedIndex]); };
        _position = Field(new ComboBox { Header = H("studio.brand.lowerThirdPosition"), ItemsSource = new[] { l["studio.brand.position.start"], l["studio.brand.position.center"], l["studio.brand.position.end"] } });
        _position.SelectionChanged += (_, _) => { if (!_syncing && _position.SelectedIndex >= 0) Edit(k => k.LowerThirdPosition = Positions[_position.SelectedIndex]); };
        _watermark = Field(new ComboBox { Header = H("studio.brand.watermark"), ItemsSource = Corners.Select(c => l["studio.brand.watermark." + BrandKit.Corner(c)]).ToList() });
        _watermark.SelectionChanged += (_, _) => { if (!_syncing && _watermark.SelectedIndex >= 0) Edit(k => k.Watermark = Corners[_watermark.SelectedIndex]); };

        var split = new Grid { ColumnSpacing = 24 };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.Children.Add(form);
        var previewBox = new StackPanel { Spacing = 6 };
        previewBox.Children.Add(Ui.Text(l["studio.brand.preview"], dim: true));
        var frame = new Border { Child = _preview, CornerRadius = new CornerRadius(6), BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(frame, l["studio.brand.previewName"]);
        ToolTipService.SetToolTip(frame, l["studio.brand.previewName"]);
        previewBox.Children.Add(frame);
        Grid.SetColumn(previewBox, 1);
        split.Children.Add(previewBox);
        Root.Children.Add(split);

        var actions = Ui.Row();
        _save = Ui.Button(l["studio.brand.save"], () => _ = _vm.SaveAsync(), accent: true);
        _revert = Ui.Button(l["studio.brand.revert"], () => _vm.Revert());
        actions.Children.Add(_save);
        actions.Children.Add(_revert);
        Root.Children.Add(actions);

        var assetsHead = new Grid();
        assetsHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        assetsHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        assetsHead.Children.Add(Ui.Heading(l["studio.assets.title"]));
        _add = Ui.Button(l["studio.assets.add"], () => _ = PickAssetAsync());
        _add.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_add, 1);
        assetsHead.Children.Add(_add);
        Root.Children.Add(assetsHead);
        Root.Children.Add(Ui.Text(l["studio.assets.body"], dim: true));
        Root.Children.Add(_usage);
        Root.Children.Add(_assets);

        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public StackPanel Root { get; }

    private ComboBox Pick(string header, Action<string?> set, Func<IReadOnlyList<string?>> ids)
    {
        var box = new ComboBox { Header = header };
        box.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var list = ids();
            var i = box.SelectedIndex;
            if (i >= 0 && i < list.Count) set(list[i]);
        };
        return box;
    }

    private void Edit(Action<BrandKit> change)
    {
        if (_syncing) return;
        _vm.Edit(change);
    }

    private void Render()
    {
        _channels.Children.Clear();
        foreach (var c in _vm.Channels)
        {
            var row = new Grid
            {
                ColumnSpacing = 12,
                Padding = new Thickness(16, 10, 16, 10),
                Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = c.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            var role = _l["studio.channels.role." + c.Role.ToString().ToLowerInvariant()];
            text.Children.Add(Ui.Text(c.Handle == null ? role : _l.Format("studio.channels.line", "@" + c.Handle, role), dim: true));
            row.Children.Add(text);
            FrameworkElement side;
            if (c.Id == _vm.Active?.Id)
            {
                side = new TextBlock { Text = _l["studio.channels.active"], Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center };
            }
            else
            {
                var channel = c;
                side = Ui.Button(_l["studio.channels.switch"], () => _vm.Switch(channel), name: _l.Format("studio.channels.switchTo", c.Name));
                side.VerticalAlignment = VerticalAlignment.Center;
            }

            Grid.SetColumn(side, 1);
            row.Children.Add(side);
            _channels.Children.Add(row);
        }

        Ui.Status(_status, _vm.IsLoading ? _l["studio.shell.loading"] : null);
        Ui.Show(_readOnly, _vm.State != null && !_vm.CanEdit ? _l["studio.brand.readOnly"] : null);
        Ui.Show(_error, _vm.ErrorKey is { } e ? _l[e] : null);
        Ui.Show(_notice, _vm.NoticeKey is { } n && !_vm.IsDirty ? _l[n] : null);

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
            _style.SelectedIndex = Array.IndexOf(Styles, k.LowerThirdStyle);
            _position.SelectedIndex = Array.IndexOf(Positions, k.LowerThirdPosition);
            _watermark.SelectedIndex = Array.IndexOf(Corners, k.Watermark);
        }
        finally
        {
            _syncing = false;
        }

        // Team members read the owner's kit: text fields stay at full contrast (read-only, not disabled).
        foreach (var w in _editors)
        {
            if (w is TextBox box) box.IsReadOnly = !_vm.CanEdit;
            else w.IsEnabled = _vm.CanEdit;
        }
        foreach (var box in _colors)
        {
            var valid = BrandKit.IsValidColor(box.Text);
            if (valid) box.ClearValue(Control.BorderBrushProperty);
            else box.BorderBrush = Ui.Brush("SystemFillColorCriticalBrush");
            AutomationProperties.SetHelpText(box, valid ? "" : "#rrggbb");
        }

        _save.IsEnabled = _vm.CanEdit && _vm.IsDirty && !_vm.IsSaving;
        _revert.IsEnabled = _vm.CanEdit && _vm.IsDirty;
        _save.Visibility = _vm.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _revert.Visibility = _vm.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _add.Visibility = _vm.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _add.IsEnabled = !_vm.IsSaving;
        DrawPreview();

        _assets.Children.Clear();
        if (lib != null)
        {
            _usage.Text = _l.Format("studio.assets.usage", _l.Number(lib.Assets.Count), _l.Number(lib.MaxAssets), Size(lib.Bytes), Size(lib.MaxTotalBytes));
            if (lib.Assets.Count == 0) _assets.Children.Add(Ui.Text(_l["studio.assets.none"], dim: true));
            foreach (var a in lib.Assets)
            {
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var kind = _l["studio.assets.kind." + a.Kind.ToString().ToLowerInvariant()];
                var icon = new SymbolIcon(a.Kind switch { StudioAssetKind.Video => Symbol.Video, StudioAssetKind.Audio => Symbol.Audio, _ => Symbol.Pictures });
                AutomationProperties.SetName(icon, kind);
                row.Children.Add(icon);
                var name = new TextBlock { Text = a.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var kindText = Ui.Text(kind, dim: true);
                kindText.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(kindText, 2);
                row.Children.Add(kindText);
                var size = Ui.Text(Size(a.Bytes), dim: true);
                size.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(size, 3);
                row.Children.Add(size);
                if (_vm.CanEdit)
                {
                    var asset = a;
                    var delete = Ui.Button(_l["studio.assets.delete"], () => _ = _vm.DeleteAssetAsync(asset), name: _l.Format("studio.assets.deleteName", a.Name));
                    Grid.SetColumn(delete, 4);
                    row.Children.Add(delete);
                }

                _assets.Children.Add(row);
            }
        }
    }

    private static void SetText(TextBox box, string text)
    {
        if (box.Text != text) box.Text = text;
    }

    private IReadOnlyList<string?> Options(ComboBox box, StudioAssetLibrary? lib, StudioAssetKind kind, string? current)
    {
        var ids = new List<string?> { null };
        var names = new List<string> { _l["studio.brand.none"] };
        foreach (var a in lib?.OfKind(kind) ?? Enumerable.Empty<StudioAsset>())
        {
            ids.Add(a.Id);
            names.Add(a.Name);
        }

        box.ItemsSource = names;
        box.SelectedIndex = Math.Max(0, ids.IndexOf(current));
        return ids;
    }

    private string Size(long bytes) => bytes >= 1_000_000_000 ? _l.Format("studio.size.gb", _l.Decimal(bytes / 1e9)) : _l.Format("studio.size.mb", _l.Decimal(Math.Round(bytes / 1e6, 1)));

    private static SolidColorBrush Color(string hex, string fallback)
    {
        var v = BrandKit.IsValidColor(hex) ? hex : fallback;
        byte P(int at) => Convert.ToByte(v.Substring(at, 2), 16);
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, P(1), P(3), P(5)));
    }

    /// <summary>A 16:9 frame in the kit's colours with the lower third and the watermark corner, mirrored in Arabic.</summary>
    private void DrawPreview()
    {
        _preview.Children.Clear();
        var k = _vm.Kit;
        var rtl = _l.IsRtl;
        double w = _preview.Width, h = _preview.Height;
        var primary = Color(k.Primary, BrandKit.DefaultPrimary);
        var secondary = Color(k.Secondary, BrandKit.DefaultSecondary);
        _preview.Background = Color(k.Background, BrandKit.DefaultBackground);

        var label = new TextBlock { Text = k.DisplayName(_l.Language) ?? _l["studio.brand.sampleName"], Foreground = Color(k.TextColor, BrandKit.DefaultText), FontSize = 15, FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        label.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = label.DesiredSize.Width, th = label.DesiredSize.Height;
        var barW = k.LowerThirdStyle == LowerThirdStyle.Minimal ? tw + 16 : Math.Max(tw + 40, w * 0.55);
        var barH = th + 18;
        var x = k.LowerThirdPosition switch
        {
            LowerThirdPosition.Center => (w - barW) / 2,
            LowerThirdPosition.End => rtl ? 16 : w - barW - 16,
            _ => rtl ? w - barW - 16 : 16,
        };
        var y = h - barH - 20;
        void Rect(double rx, double ry, double rw, double rh, Brush fill)
        {
            var r = new Rectangle { Width = Math.Max(0, rw), Height = Math.Max(0, rh), Fill = fill };
            Canvas.SetLeft(r, rx);
            Canvas.SetTop(r, ry);
            _preview.Children.Add(r);
        }

        if (k.LowerThirdStyle == LowerThirdStyle.Bar)
        {
            Rect(x, y, barW, barH, secondary);
            Rect(rtl ? x + barW - 6 : x, y, 6, barH, primary);
        }
        else if (k.LowerThirdStyle == LowerThirdStyle.Box)
        {
            Rect(x, y, barW, barH, primary);
        }
        else
        {
            Rect(x, y + barH - 3, barW, 3, primary);
        }

        var tx = k.LowerThirdPosition == LowerThirdPosition.Center ? x + ((barW - tw) / 2) : rtl ? x + barW - tw - 16 : x + 16;
        if (k.LowerThirdStyle == LowerThirdStyle.Minimal) tx = rtl ? x + barW - tw - 8 : x + 8;
        Canvas.SetLeft(label, tx);
        Canvas.SetTop(label, y + 9);
        _preview.Children.Add(label);

        if (k.Watermark != WatermarkCorner.None)
        {
            const double size = 34;
            var top = k.Watermark is WatermarkCorner.TopStart or WatermarkCorner.TopEnd;
            var startSide = k.Watermark is WatermarkCorner.TopStart or WatermarkCorner.BottomStart;
            var left = startSide != rtl;
            var wx = left ? 12 : w - size - 12;
            var wy = top ? 12 : h - size - 12;
            if (!top && Math.Abs(wy - y) < barH + 4) wy = y - size - 8;
            var dot = new Ellipse { Width = size, Height = size, Fill = primary, Opacity = 0.85 };
            Canvas.SetLeft(dot, wx);
            Canvas.SetTop(dot, wy);
            _preview.Children.Add(dot);
        }
    }

    private async Task PickAssetAsync()
    {
        if (await _pickAsset() is { } path) await _vm.AddAssetAsync(path);
    }
}
