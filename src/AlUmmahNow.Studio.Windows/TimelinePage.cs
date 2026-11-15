using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;
using AlUmmahNow.Studio.Upload;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// The editor (E19 19.5): add videos, cut them on one video track, export an MP4 with MediaComposition and queue it
/// for upload. The timeline is drawn with time running left to right in both languages (only the toolbars, labels and
/// clip list follow Arabic's direction); the clip list under it gives the same clips to the keyboard
/// and to screen readers. Titles and lower thirds sit on their own lane under the clips, are previewed over the
/// picture at the playhead, and are drawn into the export with the same painter.
/// </summary>
internal sealed class TimelinePage
{
    private const double TrackTop = 30;
    private const double TrackHeight = 64;
    private const double TitleTop = TrackTop + TrackHeight + 8;
    private const double TitleHeight = 34;
    private const double PreviewWidth = 384;
    private const double PreviewHeight = 216;

    private readonly Localizer _l;
    private readonly EditorViewModel _vm;
    private readonly UploadsViewModel _uploads;
    private readonly Func<Task<IReadOnlyList<string>>> _pickVideos;
    private readonly Func<Task<string?>> _pickProject;
    private readonly Func<string, string, string, Task<string?>> _pickSave;
    private readonly Canvas _canvas;
    private readonly ListView _clips = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 260 };
    private readonly ListView _titles = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 260 };
    private readonly TextBlock _titlesEmpty;
    private readonly Image _frame = new() { Stretch = Stretch.Uniform };
    private readonly Image _overlay = new() { Stretch = Stretch.Uniform };
    private readonly TextBox _arabic;
    private readonly TextBox _english;
    private readonly ComboBox _kind;
    private readonly GdiTitlePainter _painter;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _frameTimer;
    private (string Project, long Ms)? _frameKey;
    private (string Project, long Ms)? _frameWanted;
    private bool _frameBusy;
    private string? _overlayKey;
    private string? _shownTitleId;
    private string? _draggedTitle;
    private TitleGrip _grip;
    private bool _onTitleLane;
    private readonly TextBlock _empty;
    private readonly TextBlock _position;
    private readonly InfoBar _error = Ui.Bar(InfoBarSeverity.Error);
    private readonly StackPanel _exportRow = Ui.Row();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Width = 360, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _progressText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _doneRow = Ui.Row();
    private readonly TextBlock _done = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, Button> _buttons = new();
    private double _dragStart;
    private string? _dragged;
    private bool _rendering;

    public TimelinePage(Localizer l, EditorViewModel vm, UploadsViewModel uploads, Func<Task<IReadOnlyList<string>>> pickVideos, Func<Task<string?>> pickProject, Func<string, string, string, Task<string?>> pickSave, GdiTitlePainter painter)
    {
        _l = l;
        _vm = vm;
        _uploads = uploads;
        _painter = painter;
        _frameTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _frameTimer.Interval = TimeSpan.FromMilliseconds(150);
        _frameTimer.IsRepeating = false;
        _frameTimer.Tick += (_, _) => _ = GrabFrameAsync();
        _pickVideos = pickVideos;
        _pickProject = pickProject;
        _pickSave = pickSave;

        var project = new Grid { ColumnSpacing = 8 };
        project.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        project.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Heading(l["studio.editor.timeline"]);
        title.Margin = new Thickness(0);
        title.VerticalAlignment = VerticalAlignment.Center;
        project.Children.Add(title);
        var projectButtons = Ui.Row();
        projectButtons.Children.Add(Icon("undo", Symbol.Undo, l["studio.editor.undo"], _vm.Undo));
        projectButtons.Children.Add(Icon("redo", Symbol.Redo, l["studio.editor.redo"], _vm.Redo));
        projectButtons.Children.Add(Add("new", Ui.Button(l["studio.editor.new"], _vm.New)));
        projectButtons.Children.Add(Add("open", Ui.Button(l["studio.editor.open"], () => _ = OpenAsync())));
        projectButtons.Children.Add(Add("save", Ui.Button(l["studio.editor.save"], () => _ = SaveAsync())));
        projectButtons.Children.Add(Add("export", Ui.Button(l["studio.editor.export"], () => _ = ExportAsync())));
        projectButtons.Children.Add(Add("add", Ui.Button(l["studio.editor.addVideos"], () => _ = AddVideosAsync(), accent: true, id: "EditorAddVideos")));
        Grid.SetColumn(projectButtons, 1);
        project.Children.Add(projectButtons);

        var tools = Ui.Row();
        tools.Children.Add(Add("split", Ui.Button(l["studio.editor.split"], () => _vm.Split())));
        tools.Children.Add(Add("trimStart", Ui.Button(l["studio.editor.trimStart"], () => _vm.TrimStartToPlayhead())));
        tools.Children.Add(Add("trimEnd", Ui.Button(l["studio.editor.trimEnd"], () => _vm.TrimEndToPlayhead())));
        tools.Children.Add(Add("join", Ui.Button(l["studio.editor.join"], () => _vm.JoinWithNext())));
        tools.Children.Add(Add("earlier", Ui.Button(l["studio.editor.moveEarlier"], () => _vm.MoveBy(-1))));
        tools.Children.Add(Add("later", Ui.Button(l["studio.editor.moveLater"], () => _vm.MoveBy(1))));
        tools.Children.Add(Add("delete", Ui.Button(l["studio.editor.delete"], () => _vm.Delete())));

        _position = Ui.Text("", dim: true);

        _exportRow.Children.Add(_progress);
        _exportRow.Children.Add(_progressText);
        _exportRow.Children.Add(Ui.Button(l["studio.editor.cancelExport"], _vm.CancelExport));
        _exportRow.Visibility = Visibility.Collapsed;
        _doneRow.Children.Add(_done);
        _doneRow.Children.Add(Ui.Button(l["studio.editor.upload"], QueueUpload, accent: true));
        _doneRow.Visibility = Visibility.Collapsed;

        // Time runs left to right in both languages, so the canvas is never mirrored by the page's RTL flow.
        // The picture at the playhead with its titles, and the title editor beside it.
        var preview = new Grid
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 0, 0)),
            FlowDirection = FlowDirection.LeftToRight,
        };
        preview.Children.Add(_frame);
        preview.Children.Add(_overlay);
        AutomationProperties.SetName(preview, l["studio.editor.previewName"]);
        var previewBox = new StackPanel { Spacing = 6 };
        previewBox.Children.Add(Ui.Text(l["studio.editor.preview"], dim: true));
        previewBox.Children.Add(preview);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Ui.Heading(l["studio.editor.titles"]));
        _kind = new ComboBox { Header = l["studio.editor.titleKind"], ItemsSource = new[] { l["studio.editor.titleKind.title"], l["studio.editor.titleKind.lowerThird"] }, SelectedIndex = 1, MinWidth = 200 };
        _kind.SelectionChanged += (_, _) =>
        {
            if (_rendering || _vm.SelectedTitle == null) return;
            _vm.SetTitleKind(Kind());
        };
        panel.Children.Add(_kind);
        // The boxes sit in the page's flow; their text runs and aligns in its own language's direction (as on the Brand page).
        _arabic = new TextBox { Header = l["studio.editor.titleArabic"], TextReadingOrder = TextReadingOrder.DetectFromContent, TextAlignment = TextAlignment.DetectFromContent, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch };
        _english = new TextBox { Header = l["studio.editor.titleEnglish"], TextReadingOrder = TextReadingOrder.DetectFromContent, TextAlignment = TextAlignment.DetectFromContent, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch };
        _arabic.KeyDown += OnTitleKey;
        _english.KeyDown += OnTitleKey;
        panel.Children.Add(_arabic);
        panel.Children.Add(_english);
        var titleRow1 = Ui.Row();
        titleRow1.Children.Add(Add("addTitle", Ui.Button(l["studio.editor.addTitle"], SubmitTitle, accent: true, id: "EditorAddTitle")));
        titleRow1.Children.Add(Add("saveTitle", Ui.Button(l["studio.editor.saveTitle"], SubmitTitle, accent: true, id: "EditorSaveTitle")));
        titleRow1.Children.Add(Add("newTitle", Ui.Button(l["studio.editor.newTitle"], () => _vm.SelectTitle(null))));
        titleRow1.Children.Add(Add("removeTitle", Ui.Button(l["studio.editor.removeTitle"], () => _vm.RemoveTitle())));
        var titleRow2 = Ui.Row();
        titleRow2.Children.Add(Add("titleStart", Ui.Button(l["studio.editor.titleStart"], () => _vm.TitleStartToPlayhead())));
        titleRow2.Children.Add(Add("titleEnd", Ui.Button(l["studio.editor.titleEnd"], () => _vm.TitleEndToPlayhead())));
        titleRow2.Children.Add(Add("titleMove", Ui.Button(l["studio.editor.titleMove"], () => _vm.MoveTitleToPlayhead())));
        panel.Children.Add(titleRow1);
        panel.Children.Add(titleRow2);
        panel.Children.Add(Ui.Text(l["studio.editor.titleBrand"], dim: true));

        var stage = new Grid { ColumnSpacing = 16 };
        stage.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stage.Children.Add(previewBox);
        Grid.SetColumn(panel, 1);
        stage.Children.Add(panel);

        _canvas = new Canvas
        {
            Height = TitleTop + TitleHeight + 12,
            FlowDirection = FlowDirection.LeftToRight,
            Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"),
            IsTabStop = true,
            UseSystemFocusVisuals = true,
        };
        AutomationProperties.SetName(_canvas, l["studio.editor.timeline"]);
        var frame = new Border
        {
            Child = _canvas,
            BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
        _canvas.SizeChanged += (_, _) => Draw();
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerReleased += OnReleased;
        _canvas.KeyDown += OnKey;
        _canvas.ActualThemeChanged += (_, _) => Draw();

        _empty = Ui.Text(l["studio.editor.empty"], dim: true);
        _clips.SelectionChanged += (_, _) =>
        {
            if (_rendering || _clips.SelectedIndex < 0) return;
            if (_vm.Clips.ElementAtOrDefault(_clips.SelectedIndex) is not { } clip) return;
            _vm.Seek(clip.TimelineStartMs);
            _vm.Select(clip.ClipId);
        };
        AutomationProperties.SetName(_clips, l["studio.editor.clips"]);
        _titlesEmpty = Ui.Text(l["studio.editor.titlesEmpty"], dim: true);
        _titles.SelectionChanged += (_, _) =>
        {
            if (_rendering || _titles.SelectedIndex < 0) return;
            if (_vm.Titles.ElementAtOrDefault(_titles.SelectedIndex) is not { } title) return;
            _vm.Seek(title.StartMs);
            _vm.SelectTitle(title.Id);
        };
        AutomationProperties.SetName(_titles, l["studio.editor.titles"]);

        // Clips and titles side by side, each a list the keyboard and screen readers can use.
        var lists = new Grid { ColumnSpacing = 16 };
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var clipsColumn = new StackPanel { Spacing = 8 };
        clipsColumn.Children.Add(Ui.Heading(l["studio.editor.clips"]));
        clipsColumn.Children.Add(_clips);
        lists.Children.Add(clipsColumn);
        var titlesColumn = new StackPanel { Spacing = 8 };
        titlesColumn.Children.Add(Ui.Heading(l["studio.editor.titles"]));
        titlesColumn.Children.Add(_titlesEmpty);
        titlesColumn.Children.Add(_titles);
        Grid.SetColumn(titlesColumn, 1);
        lists.Children.Add(titlesColumn);

        var page = new StackPanel { Spacing = 12, Padding = new Thickness(24, 16, 24, 24) };
        page.Children.Add(project);
        page.Children.Add(tools);
        page.Children.Add(_position);
        page.Children.Add(_error);
        page.Children.Add(_exportRow);
        page.Children.Add(_doneRow);
        page.Children.Add(stage);
        page.Children.Add(frame);
        page.Children.Add(_empty);
        page.Children.Add(Ui.Text(l["studio.editor.hint"], dim: true));
        page.Children.Add(lists);
        Root = new ScrollViewer { Content = page };

        var undo = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Z, Modifiers = global::Windows.System.VirtualKeyModifiers.Control };
        undo.Invoked += (_, e) => { _vm.Undo(); e.Handled = true; };
        var redo = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Y, Modifiers = global::Windows.System.VirtualKeyModifiers.Control };
        redo.Invoked += (_, e) => { _vm.Redo(); e.Handled = true; };
        page.KeyboardAccelerators.Add(undo);
        page.KeyboardAccelerators.Add(redo);

        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public ScrollViewer Root { get; }

    /// <summary>CI scene: two videos, the first split in the middle and its second half selected.</summary>
    public async Task RunSceneAsync(IReadOnlyList<string> files)
    {
        await _vm.AddVideosAsync(files);
        if (_vm.Clips.FirstOrDefault() is { } first)
        {
            _vm.ClickAt(first.TimelineStartMs + (first.TimelineDurationMs / 2));
            _vm.Split();
            _vm.ClickAt(_vm.PlayheadMs + 400);
        }
    }

    /// <summary>CI scene: the cut, then a bilingual lower third and a centred title; the playhead on the lower third.</summary>
    public async Task RunTitlesSceneAsync(IReadOnlyList<string> files)
    {
        await RunSceneAsync(files);
        foreach (var (ms, kind, ar, en) in EditorScenes.Titles)
        {
            _vm.Seek(ms);
            _vm.AddTitle(kind, ar, en);
        }

        _vm.Seek(EditorScenes.PreviewMs);
        _vm.SelectTitle(_vm.TitlesOnScreen.FirstOrDefault()?.Id);
    }

    private TitleKind Kind() => _kind.SelectedIndex == 0 ? TitleKind.Title : TitleKind.LowerThird;

    private void SubmitTitle()
    {
        if (_vm.SelectedTitle != null) _vm.SetTitleText(_arabic.Text, _english.Text);
        else if (_vm.AddTitle(Kind(), _arabic.Text, _english.Text)) _shownTitleId = _vm.SelectedTitleId;
    }

    private void OnTitleKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter) return;
        SubmitTitle();
        e.Handled = true;
    }

    private Button Add(string name, Button button)
    {
        _buttons[name] = button;
        return button;
    }

    private Button Icon(string name, Symbol symbol, string label, Action click)
    {
        var b = new Button { Content = new SymbolIcon(symbol) };
        AutomationProperties.SetName(b, label);
        ToolTipService.SetToolTip(b, label);
        b.Click += (_, _) => click();
        return Add(name, b);
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            var clips = _vm.Clips;
            var busy = _vm.IsBusy || _vm.IsExporting;
            _buttons["split"].IsEnabled = !busy && _vm.CanSplit;
            _buttons["trimStart"].IsEnabled = !busy && _vm.CanTrimStart;
            _buttons["trimEnd"].IsEnabled = !busy && _vm.CanTrimEnd;
            _buttons["join"].IsEnabled = !busy && _vm.CanJoin;
            var index = _vm.Target is { } t ? clips.ToList().FindIndex(c => c.ClipId == t.ClipId) : -1;
            _buttons["earlier"].IsEnabled = !busy && index > 0;
            _buttons["later"].IsEnabled = !busy && index >= 0 && index < clips.Count - 1;
            _buttons["delete"].IsEnabled = !busy && _vm.Target != null;
            _buttons["undo"].IsEnabled = !busy && _vm.CanUndo;
            _buttons["redo"].IsEnabled = !busy && _vm.CanRedo;
            _buttons["export"].IsEnabled = _vm.CanExport;
            _buttons["save"].IsEnabled = !busy && clips.Count > 0;
            _buttons["add"].IsEnabled = !busy;
            _buttons["open"].IsEnabled = !busy;
            _buttons["new"].IsEnabled = !busy && (clips.Count > 0 || _vm.CanUndo);
            var title = _vm.SelectedTitle;
            _buttons["addTitle"].Visibility = title == null ? Visibility.Visible : Visibility.Collapsed;
            _buttons["addTitle"].IsEnabled = !busy && _vm.CanAddTitle;
            _buttons["saveTitle"].Visibility = title != null ? Visibility.Visible : Visibility.Collapsed;
            _buttons["saveTitle"].IsEnabled = !busy;
            foreach (var name in new[] { "newTitle", "titleStart", "titleEnd", "titleMove", "removeTitle" }) _buttons[name].IsEnabled = !busy && title != null;
            if (title?.Id != _shownTitleId)
            {
                // Selecting a title shows its text; leaving it clears the boxes for the next one.
                _shownTitleId = title?.Id;
                _arabic.Text = title?.In("ar") ?? "";
                _english.Text = title?.In("en") ?? "";
            }

            if (title != null) _kind.SelectedIndex = TitleTemplates.KindOf(title) == TitleKind.Title ? 0 : 1;

            var titles = _vm.Titles;
            _titlesEmpty.Visibility = titles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _titles.Items.Clear();
            foreach (var item in titles)
            {
                var kind = _l[TitleTemplates.KindOf(item) == TitleKind.Title ? "studio.editor.titleKind.title" : "studio.editor.titleKind.lowerThird"];
                _titles.Items.Add(new TextBlock { Text = _l.LineStart + _l.Format("studio.editor.titleItem", Localizer.Isolate(TitleTemplates.Label(item, _l.Language)), kind, Localizer.IsolateLtr(EditorTime.Format(item.StartMs)), Localizer.IsolateLtr(EditorTime.Format(item.EndMs))) });
            }

            _titles.SelectedIndex = title != null ? titles.ToList().FindIndex(t => t.Id == title.Id) : -1;

            _empty.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _position.Text = _l.Format("studio.editor.playhead", Localizer.IsolateLtr(EditorTime.Format(_vm.PlayheadMs))) + " · " +
                _l.Format("studio.editor.length", Localizer.IsolateLtr(EditorTime.Format(_vm.Timeline.DurationMs)));
            Ui.Show(_error, _vm.ErrorKey is { } key ? (_vm.ErrorDetail is { } d ? _l.Format(key, Localizer.Isolate(d)) : _l[key]) : null);
            _exportRow.Visibility = _vm.IsExporting ? Visibility.Visible : Visibility.Collapsed;
            _progress.Value = _vm.ExportProgress;
            _progressText.Text = _l.Format("studio.editor.exporting", _l.Number((long)Math.Round(_vm.ExportProgress * 100)));
            _doneRow.Visibility = _vm.ExportedPath != null && !_vm.IsExporting ? Visibility.Visible : Visibility.Collapsed;
            if (_vm.ExportedPath is { } exported) _done.Text = _l.Format("studio.editor.exported", Localizer.Isolate(Path.GetFileName(exported)));

            _clips.Items.Clear();
            foreach (var c in clips)
            {
                var text = _l.LineStart + _l.Format("studio.editor.clip", Localizer.Isolate(Name(c)), Localizer.IsolateLtr(EditorTime.Format(c.TimelineStartMs)), Localizer.IsolateLtr(EditorTime.Format(EditorViewModel.End(c))));
                _clips.Items.Add(new TextBlock { Text = text });
            }

            _clips.SelectedIndex = _vm.Selected is { } s ? clips.ToList().FindIndex(c => c.ClipId == s.ClipId) : -1;
        }
        finally
        {
            _rendering = false;
        }

        Draw();
        _ = DrawOverlayAsync();
        RequestFrame();
    }

    /// <summary>The titles at the playhead, drawn by the export's painter onto a transparent picture over the frame.</summary>
    private async Task DrawOverlayAsync()
    {
        var settings = _vm.Settings;
        var titles = _vm.TitlesOnScreen;
        var decor = _vm.Decor;
        var key = string.Join("|", titles.Select(t => t.Id + "@" + t.StartMs + "+" + t.DurationMs + ":" + string.Join(",", t.Text.Values) + t.Style.GetHashCode()))
            + $"#{settings.Width}x{settings.Height}#{decor.Style}{decor.Side}{decor.Accent}{decor.Logo?.Length}";
        if (key == _overlayKey) return;
        _overlayKey = key;
        if (titles.Count == 0)
        {
            _overlay.Source = null;
            return;
        }

        using var bitmap = _painter.Render(titles, decor, settings.Width, settings.Height);
        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        png.Position = 0;
        var image = new BitmapImage();
        await image.SetSourceAsync(png.AsRandomAccessStream());
        if (key == _overlayKey) _overlay.Source = image;
    }

    /// <summary>Asks MediaComposition for the picture at the playhead, at most one at a time; the newest request wins.</summary>
    private void RequestFrame()
    {
        if (_vm.Clips.Count == 0)
        {
            _frame.Source = null;
            _frameKey = null;
            return;
        }

        var key = (_vm.Project, _vm.PlayheadMs);
        if (_frameKey == key || _frameWanted == key) return;
        _frameWanted = key;
        _frameTimer.Stop();
        _frameTimer.Start();
    }

    private async Task GrabFrameAsync()
    {
        if (_frameBusy || _frameWanted is not { } key) return;
        _frameBusy = true;
        try
        {
            var settings = _vm.Settings;
            var height = (int)PreviewHeight * 2;
            var width = Math.Max(2, (int)Math.Round(height * (double)settings.Width / Math.Max(1, settings.Height)));
            using var stream = await MediaCompositionRenderer.FrameAsync(_vm.Timeline, key.Ms, width, height, CancellationToken.None);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            _frame.Source = image;
            _frameKey = key;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is NotSupportedException || ex is System.Runtime.InteropServices.COMException || ex is UnauthorizedAccessException)
        {
            // The preview still shows the titles, over black.
            AlUmmahNow.CoreBridge.Logging.Log.For("editor").Warn("no preview frame", ex);
            _frame.Source = null;
            _frameKey = key;
        }
        finally
        {
            _frameBusy = false;
        }

        if (_frameWanted is { } wanted && wanted != _frameKey) _ = GrabFrameAsync();
    }

    private TimelineViewport Viewport() => TimelineViewport.Fit(_vm.Timeline.DurationMs, Math.Max(1, _canvas.ActualWidth), EditorTime.TimeIsRtl);

    private void Draw()
    {
        _canvas.Children.Clear();
        if (_canvas.ActualWidth <= 0) return;
        var v = Viewport();
        var dim = Ui.Brush("TextFillColorSecondaryBrush");
        var duration = Math.Max(1000, _vm.Timeline.DurationMs);
        var step = duration > 120_000 ? 10_000 : duration > 30_000 ? 5_000 : 1_000;
        for (long ms = 0; ms <= duration; ms += step)
        {
            var x = v.ToX(ms);
            var major = ms % (step * 5) == 0;
            var tick = new Rectangle { Width = 1, Height = major ? 18 : 10, Fill = dim };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, major ? 4 : 12);
            _canvas.Children.Add(tick);
            if (major) Place(new TextBlock { Text = EditorTime.Format(ms), FontSize = 11, Foreground = dim }, x + 4, 2, alignEnd: false);
        }

        var fill = Ui.Brush("AccentFillColorDefaultBrush");
        var onFill = Ui.Brush("TextOnAccentFillColorPrimaryBrush");
        var caution = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 160, 55));
        foreach (var c in _vm.Clips)
        {
            var a = v.ToX(c.TimelineStartMs);
            var b = v.ToX(EditorViewModel.End(c));
            var left = Math.Min(a, b) + 1;
            var w = Math.Max(2, Math.Abs(b - a) - 2);
            var selected = c.ClipId == _vm.SelectedClipId;
            var block = new Border
            {
                Width = w,
                Height = TrackHeight,
                CornerRadius = new CornerRadius(6),
                Background = fill,
                BorderBrush = caution,
                BorderThickness = new Thickness(selected ? 3 : 0),
                Padding = new Thickness(8, 6, 8, 6),
                Child = new StackPanel
                {
                    // Labels start at the clip's start (its left edge); Arabic names still shape right to left inside.
                    FlowDirection = FlowDirection.LeftToRight,
                    Children =
                    {
                        new TextBlock { Text = Name(c), Foreground = onFill, TextTrimming = TextTrimming.CharacterEllipsis, TextReadingOrder = TextReadingOrder.DetectFromContent },
                        new TextBlock { Text = EditorTime.Format(c.TimelineDurationMs), Foreground = onFill, FontSize = 11, Opacity = 0.85 },
                    },
                },
            };
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, TrackTop);
            _canvas.Children.Add(block);
        }

        // The title lane: time still runs left to right; each label shapes in its own direction inside its block.
        var titleFill = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 107, 84, 158));
        var white = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255));
        foreach (var t in _vm.Titles)
        {
            var a = v.ToX(t.StartMs);
            var b = v.ToX(t.EndMs);
            var left = Math.Min(a, b) + 1;
            var w = Math.Max(2, Math.Abs(b - a) - 2);
            var block = new Border
            {
                Width = w,
                Height = TitleHeight,
                CornerRadius = new CornerRadius(5),
                Background = titleFill,
                BorderBrush = caution,
                BorderThickness = new Thickness(t.Id == _vm.SelectedTitleId ? 3 : 0),
                Padding = new Thickness(8, 4, 8, 4),
                Child = new TextBlock { Text = TitleTemplates.Label(t, _l.Language), Foreground = white, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextReadingOrder = TextReadingOrder.DetectFromContent, VerticalAlignment = VerticalAlignment.Center },
            };
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, TitleTop);
            _canvas.Children.Add(block);
        }

        var head = v.ToX(_vm.PlayheadMs);
        var line = new Rectangle { Width = 2, Height = _canvas.Height, Fill = caution };
        Canvas.SetLeft(line, head - 1);
        _canvas.Children.Add(line);
        var cap = new Polygon { Fill = caution, Points = { new global::Windows.Foundation.Point(head - 6, 0), new global::Windows.Foundation.Point(head + 6, 0), new global::Windows.Foundation.Point(head, 8) } };
        _canvas.Children.Add(cap);
    }

    private void Place(TextBlock text, double x, double y, bool alignEnd)
    {
        text.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(text, alignEnd ? x - text.DesiredSize.Width : x);
        Canvas.SetTop(text, y);
        _canvas.Children.Add(text);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _canvas.Focus(FocusState.Pointer);
        _canvas.CapturePointer(e.Pointer);
        var point = e.GetCurrentPoint(_canvas).Position;
        _dragStart = point.X;
        var v = Viewport();
        var ms = v.ToMs(_dragStart);
        _dragged = null;
        _draggedTitle = null;
        _grip = TitleGrip.None;
        _onTitleLane = point.Y >= TitleTop - 4;
        if (_onTitleLane)
        {
            // The selected title's edges win, so a short title can still be retimed.
            var title = _vm.SelectedTitle is { } s && TitleHit.At(v, s, _dragStart) != TitleGrip.None ? s : _vm.Titles.FirstOrDefault(t => TitleHit.At(v, t, _dragStart) != TitleGrip.None);
            if (title != null)
            {
                _draggedTitle = title.Id;
                _grip = TitleHit.At(v, title, _dragStart);
            }

            _vm.ClickTitleAt(ms);
            if (title != null) _vm.SelectTitle(title.Id);
            return;
        }

        _dragged = _vm.ClipAt(ms)?.ClipId;
        _vm.ClickAt(ms);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _canvas.ReleasePointerCapture(e.Pointer);
        var x = e.GetCurrentPoint(_canvas).Position.X;
        // A drag of a few pixels is a click; a longer one moves the clip or title by that much time.
        if (Math.Abs(x - _dragStart) < 6) return;
        var v = Viewport();
        var delta = v.ToMs(x) - v.ToMs(_dragStart);
        if (_onTitleLane)
        {
            if (_draggedTitle == null || _vm.Titles.FirstOrDefault(t => t.Id == _draggedTitle) is not { } t) return;
            _vm.SelectTitle(t.Id);
            if (_grip == TitleGrip.Start) _vm.RetimeTitle(t.StartMs + delta, t.EndMs);
            else if (_grip == TitleGrip.End) _vm.RetimeTitle(t.StartMs, t.EndMs + delta);
            else _vm.MoveTitleTo(t.StartMs + delta);
            _draggedTitle = null;
            return;
        }

        if (_dragged == null || _vm.Selected is not { } c || c.ClipId != _dragged) return;
        _vm.MoveTo(c.TimelineStartMs + delta);
        _dragged = null;
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        var step = shift ? 1000 : 100;
        // Time runs left to right in both languages, so Right always moves forward.
        var forward = global::Windows.System.VirtualKey.Right;
        var back = global::Windows.System.VirtualKey.Left;
        if (e.Key == forward) _vm.Seek(_vm.PlayheadMs + step);
        else if (e.Key == back) _vm.Seek(_vm.PlayheadMs - step);
        else if (e.Key == global::Windows.System.VirtualKey.Delete)
        {
            if (_vm.SelectedTitle != null) _vm.RemoveTitle();
            else _vm.Delete();
        }
        else if (e.Key == global::Windows.System.VirtualKey.S) _vm.Split();
        else return;
        e.Handled = true;
    }

    private string Name(FlatClip c) => _vm.SourceNames.TryGetValue(c.SourceId, out var n) ? n : c.SourceId;

    private async Task AddVideosAsync()
    {
        var files = await _pickVideos();
        if (files.Count > 0) await _vm.AddVideosAsync(files);
    }

    private async Task OpenAsync()
    {
        if (await _pickProject() is { } path) await _vm.OpenAsync(path);
    }

    private async Task SaveAsync()
    {
        if (await _pickSave(_l["studio.editor.projectFile"], _l["studio.editor.projectFile"], EditorTime.ProjectExtension) is { } path) await _vm.SaveAsync(path);
    }

    private async Task ExportAsync()
    {
        if (await _pickSave(_l["studio.editor.exportName"], "MP4", ".mp4") is { } path) await _vm.ExportAsync(path);
    }

    private void QueueUpload()
    {
        if (_vm.ExportedPath is not { } path) return;
        _uploads.AddFiles(new[] { path });
        _done.Text = _l["studio.editor.uploadAdded"];
    }
}
