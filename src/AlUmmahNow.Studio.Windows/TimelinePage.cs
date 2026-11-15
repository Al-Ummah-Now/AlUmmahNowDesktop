using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;
using AlUmmahNow.Studio.Upload;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// The editor (E19 19.5): add videos, cut them on one video track, export an MP4 with MediaComposition and queue it
/// for upload. The timeline is drawn (mirrored in Arabic); the clip list under it gives the same clips to the keyboard
/// and to screen readers.
/// </summary>
internal sealed class TimelinePage
{
    private const double TrackTop = 30;
    private const double TrackHeight = 64;

    private readonly Localizer _l;
    private readonly EditorViewModel _vm;
    private readonly UploadsViewModel _uploads;
    private readonly Func<Task<IReadOnlyList<string>>> _pickVideos;
    private readonly Func<Task<string?>> _pickProject;
    private readonly Func<string, string, string, Task<string?>> _pickSave;
    private readonly Canvas _canvas;
    private readonly ListView _clips = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 260 };
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

    public TimelinePage(Localizer l, EditorViewModel vm, UploadsViewModel uploads, Func<Task<IReadOnlyList<string>>> pickVideos, Func<Task<string?>> pickProject, Func<string, string, string, Task<string?>> pickSave)
    {
        _l = l;
        _vm = vm;
        _uploads = uploads;
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
        projectButtons.Children.Add(Add("add", Ui.Button(l["studio.editor.addVideos"], () => _ = AddVideosAsync(), accent: true, name: "EditorAddVideos")));
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

        // The canvas computes mirrored coordinates itself, so it must not be mirrored again.
        _canvas = new Canvas
        {
            Height = TrackTop + TrackHeight + 16,
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

        var page = new StackPanel { Spacing = 12, Padding = new Thickness(24, 16, 24, 24) };
        page.Children.Add(project);
        page.Children.Add(tools);
        page.Children.Add(_position);
        page.Children.Add(_error);
        page.Children.Add(_exportRow);
        page.Children.Add(_doneRow);
        page.Children.Add(frame);
        page.Children.Add(_empty);
        page.Children.Add(Ui.Text(l["studio.editor.hint"], dim: true));
        page.Children.Add(Ui.Heading(l["studio.editor.clips"]));
        page.Children.Add(_clips);
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
                var text = _l.Format("studio.editor.clip", Localizer.Isolate(Name(c)), Localizer.IsolateLtr(EditorTime.Format(c.TimelineStartMs)), Localizer.IsolateLtr(EditorTime.Format(EditorViewModel.End(c))));
                _clips.Items.Add(new TextBlock { Text = text, TextReadingOrder = TextReadingOrder.DetectFromContent });
            }

            _clips.SelectedIndex = _vm.Selected is { } s ? clips.ToList().FindIndex(c => c.ClipId == s.ClipId) : -1;
        }
        finally
        {
            _rendering = false;
        }

        Draw();
    }

    private TimelineViewport Viewport() => TimelineViewport.Fit(_vm.Timeline.DurationMs, Math.Max(1, _canvas.ActualWidth), _l.IsRtl);

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
            if (major) Place(new TextBlock { Text = EditorTime.Format(ms), FontSize = 11, Foreground = dim }, x + (_l.IsRtl ? -4 : 4), 2, _l.IsRtl);
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
                    FlowDirection = _l.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
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
        _dragStart = e.GetCurrentPoint(_canvas).Position.X;
        var ms = Viewport().ToMs(_dragStart);
        _dragged = _vm.ClipAt(ms)?.ClipId;
        _vm.ClickAt(ms);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _canvas.ReleasePointerCapture(e.Pointer);
        var x = e.GetCurrentPoint(_canvas).Position.X;
        // A drag of a few pixels is a click; a longer one moves the clip by that much time.
        if (_dragged == null || Math.Abs(x - _dragStart) < 6 || _vm.Selected is not { } c || c.ClipId != _dragged) return;
        var v = Viewport();
        _vm.MoveTo(c.TimelineStartMs + (v.ToMs(x) - v.ToMs(_dragStart)));
        _dragged = null;
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        var step = shift ? 1000 : 100;
        var forward = _l.IsRtl ? global::Windows.System.VirtualKey.Left : global::Windows.System.VirtualKey.Right;
        var back = _l.IsRtl ? global::Windows.System.VirtualKey.Right : global::Windows.System.VirtualKey.Left;
        if (e.Key == forward) _vm.Seek(_vm.PlayheadMs + step);
        else if (e.Key == back) _vm.Seek(_vm.PlayheadMs - step);
        else if (e.Key == global::Windows.System.VirtualKey.Delete) _vm.Delete();
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
