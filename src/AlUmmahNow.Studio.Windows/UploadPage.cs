using System.Globalization;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Path = System.IO.Path;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// Upload: stage videos or a folder, set values for all of them, upload, and follow the queue.
/// Queue rows are updated in place so keyboard and Narrator focus stay put.
/// </summary>
internal sealed class UploadPage
{
    private readonly Localizer _l;
    private readonly UploadsViewModel _vm;
    private readonly StudioShellViewModel _shell;
    private readonly Func<XamlRoot?> _root;
    private readonly FlowDirection _direction;
    private readonly StackPanel _staged = new() { Spacing = 6 };
    private readonly TextBlock _stagedTitle = new() { Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
    private readonly Button _start = new() { Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _clear;
    private readonly StackPanel _queue = new() { Spacing = 8 };
    private readonly TextBlock _queueEmpty;
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly TextBlock _watching = new() { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _stopWatching;
    private readonly StackPanel _playlists = new() { Spacing = 2 };
    private readonly StackPanel _scheduleRow = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly CheckBox _recursive;
    private readonly Dictionary<string, QueueRow> _rows = new();
    private IReadOnlyList<PlannedUpload>? _renderedStaged;

    public UploadPage(Localizer l, UploadsViewModel vm, StudioShellViewModel shell, Func<XamlRoot?> root, Func<Task<IReadOnlyList<string>>> pickFiles, Func<Task<string?>> pickFolder, Action<string?> rememberWatchFolder)
    {
        _l = l;
        _vm = vm;
        _shell = shell;
        _root = root;
        _direction = l.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Root = new StackPanel { Spacing = 14, Padding = new Thickness(20) };

        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        add.Children.Add(Button(l["studio.upload.addFiles"], async () => _vm.AddFiles(await pickFiles()), accent: true, id: "AddFiles"));
        add.Children.Add(Button(l["studio.upload.addFolder"], async () =>
        {
            if (await pickFolder() is { } f) _vm.AddFolder(f, _recursive!.IsChecked == true);
        }, id: "AddFolder"));
        _recursive = new CheckBox { Content = l["studio.upload.includeSubfolders"] };
        add.Children.Add(_recursive);
        add.Children.Add(Button(l["studio.upload.watch"], async () =>
        {
            if (await pickFolder() is not { } f) return;
            _vm.Watch(f);
            rememberWatchFolder(f);
        }));
        Root.Children.Add(add);
        var watchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        watchRow.Children.Add(_watching);
        _stopWatching = Button(l["studio.upload.stopWatching"], () =>
        {
            _vm.Watch(null);
            rememberWatchFolder(null);
            return Task.CompletedTask;
        });
        watchRow.Children.Add(_stopWatching);
        Root.Children.Add(watchRow);
        Root.Children.Add(_error);

        var expander = new Expander { Header = l["studio.upload.forAll"], IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = BuildDefaults() };
        Root.Children.Add(expander);

        AutomationProperties.SetHeadingLevel(_stagedTitle, AutomationHeadingLevel.Level2);
        Root.Children.Add(_stagedTitle);
        Root.Children.Add(_staged);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AutomationProperties.SetAutomationId(_start, "StartUpload");
        _start.Click += (_, _) => _vm.Start();
        actions.Children.Add(_start);
        _clear = Button(l["studio.upload.clear"], () =>
        {
            _vm.ClearStaged();
            return Task.CompletedTask;
        });
        actions.Children.Add(_clear);
        Root.Children.Add(actions);

        var queueTitle = new TextBlock { Text = l["studio.upload.queue"], Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(queueTitle, AutomationHeadingLevel.Level2);
        var queueHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        queueHeader.Children.Add(queueTitle);
        queueHeader.Children.Add(Button(l["studio.upload.clearFinished"], () =>
        {
            _vm.ClearFinished();
            return Task.CompletedTask;
        }));
        Root.Children.Add(queueHeader);
        _queueEmpty = new TextBlock { Text = l["studio.upload.queueEmpty"], Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        Root.Children.Add(_queueEmpty);
        Root.Children.Add(_queue);

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(UploadsViewModel.Staged): RenderStaged(); break;
                case nameof(UploadsViewModel.Items): RenderQueue(); break;
                case nameof(UploadsViewModel.Playlists): RenderPlaylists(); break;
                case nameof(UploadsViewModel.ErrorKey): RenderError(); break;
                case nameof(UploadsViewModel.WatchFolder): RenderWatching(); break;
            }
        };
        _shell.ChannelChanged += (_, _) => RenderStaged();
        RenderStaged();
        RenderQueue();
        RenderPlaylists();
        RenderWatching();
        RenderError();
    }

    public StackPanel Root { get; }

    private UploadDefaults D => _vm.Defaults;

    private static Button Button(string text, Func<Task> click, bool accent = false, string? id = null)
    {
        var b = new Button { Content = text };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (id != null) AutomationProperties.SetAutomationId(b, id);
        b.Click += async (_, _) => await click();
        return b;
    }

    private FrameworkElement BuildDefaults()
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

        var title = new TextBox { Header = _l["studio.upload.titlePattern"], Text = D.TitlePattern, Description = _l["studio.upload.titlePatternHint"] };
        title.TextChanged += (_, _) =>
        {
            D.TitlePattern = title.Text;
            _vm.ApplyDefaults();
        };
        panel.Children.Add(title);

        var description = new TextBox { Header = _l["studio.upload.description"], AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
        description.TextChanged += (_, _) =>
        {
            D.Description = description.Text;
            _vm.ApplyDefaults();
        };
        panel.Children.Add(description);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var categories = new[] { (string?)null }.Concat(StudioCatalog.Categories).ToList();
        var category = new ComboBox { Header = _l["studio.upload.category"], ItemsSource = categories.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.category." + c]).ToList(), SelectedIndex = 0, MinWidth = 200 };
        category.SelectionChanged += (_, _) =>
        {
            D.Category = categories[Math.Max(0, category.SelectedIndex)];
            _vm.ApplyDefaults();
        };
        var languages = new[] { (string?)null }.Concat(StudioCatalog.Languages).ToList();
        var language = new ComboBox { Header = _l["studio.upload.language"], ItemsSource = languages.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.language." + c]).ToList(), SelectedIndex = 0, MinWidth = 160 };
        language.SelectionChanged += (_, _) =>
        {
            D.Language = languages[Math.Max(0, language.SelectedIndex)];
            _vm.ApplyDefaults();
        };
        var visibilities = new[] { UploadVisibility.Private, UploadVisibility.Public, UploadVisibility.Scheduled };
        var visibility = new ComboBox { Header = _l["studio.upload.visibility"], ItemsSource = visibilities.Select(v => _l["studio.upload.visibility." + v.ToString().ToLowerInvariant()]).ToList(), SelectedIndex = 0, MinWidth = 160 };
        visibility.SelectionChanged += (_, _) =>
        {
            D.Visibility = visibilities[Math.Max(0, visibility.SelectedIndex)];
            _scheduleRow.Visibility = D.Visibility == UploadVisibility.Scheduled ? Visibility.Visible : Visibility.Collapsed;
            _vm.ApplyDefaults();
        };
        row.Children.Add(category);
        row.Children.Add(language);
        row.Children.Add(visibility);
        panel.Children.Add(row);

        D.TimeZone = Formats.LocalTimeZone();
        D.ScheduleStart ??= DateTimeOffset.Now.Date.AddDays(1).AddHours(9);
        var start = new TextBox { Header = _l["studio.upload.scheduleStart"], Text = Formats.ToLocalInput(D.ScheduleStart.Value), MinWidth = 220 };
        start.TextChanged += (_, _) =>
        {
            var t = Formats.ParseLocal(start.Text);
            start.Description = t == null ? _l["studio.upload.scheduleInvalid"] : null;
            if (t == null) return;
            D.ScheduleStart = t;
            _vm.ApplyDefaults();
        };
        var every = new NumberBox { Header = _l["studio.upload.scheduleEvery"], Value = D.ScheduleInterval.TotalHours, Minimum = 0, Maximum = 24 * 30, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1 };
        every.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(every.Value)) return;
            D.ScheduleInterval = TimeSpan.FromHours(every.Value);
            _vm.ApplyDefaults();
        };
        var scheduleFields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        scheduleFields.Children.Add(start);
        scheduleFields.Children.Add(every);
        _scheduleRow.Children.Add(scheduleFields);
        panel.Children.Add(_scheduleRow);

        var playlistsHeader = new TextBlock { Text = _l["studio.upload.playlists"] };
        panel.Children.Add(playlistsHeader);
        panel.Children.Add(_playlists);
        var newPlaylist = new TextBox { PlaceholderText = _l["studio.upload.newPlaylist"], MinWidth = 240 };
        AutomationProperties.SetName(newPlaylist, _l["studio.upload.newPlaylist"]);
        var create = Button(_l["studio.upload.createPlaylist"], async () =>
        {
            var p = await _vm.CreatePlaylistAsync(newPlaylist.Text, isPublic: true);
            if (p == null) return;
            D.PlaylistIds.Add(p.Id);
            newPlaylist.Text = "";
            _vm.ApplyDefaults();
            RenderPlaylists();
        });
        var createRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        createRow.Children.Add(newPlaylist);
        createRow.Children.Add(create);
        panel.Children.Add(createRow);

        var tags = new TextBox { Header = _l["studio.upload.tags"] };
        tags.TextChanged += (_, _) =>
        {
            D.Tags = UploadPlanner.ParseTags(tags.Text);
            _vm.ApplyDefaults();
        };
        panel.Children.Add(tags);
        return panel;
    }

    private void RenderPlaylists()
    {
        _playlists.Children.Clear();
        foreach (var p in _vm.Playlists)
        {
            var check = new CheckBox { Content = p.Title, IsChecked = D.PlaylistIds.Contains(p.Id) };
            check.Checked += (_, _) =>
            {
                if (!D.PlaylistIds.Contains(p.Id)) D.PlaylistIds.Add(p.Id);
                _vm.ApplyDefaults();
            };
            check.Unchecked += (_, _) =>
            {
                D.PlaylistIds.Remove(p.Id);
                _vm.ApplyDefaults();
            };
            _playlists.Children.Add(check);
        }
    }

    private void RenderError()
    {
        _error.Message = _vm.ErrorKey is { } k ? _l[k] : "";
        _error.IsOpen = _vm.ErrorKey != null;
    }

    private void RenderWatching()
    {
        _watching.Text = _vm.WatchFolder is { } f ? _l.Format("studio.upload.watchingFolder", Localizer.Isolate(f)) : "";
        _stopWatching.Visibility = _vm.WatchFolder != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderStaged()
    {
        var staged = _vm.Staged;
        _start.Content = staged.Count == 1 ? _l["studio.upload.startOne"] : _l.Format("studio.upload.start", staged.Count);
        _start.IsEnabled = _vm.CanStart;
        _start.Visibility = _clear.Visibility = staged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _stagedTitle.Text = staged.Count > 0
            ? _l.Format("studio.upload.stagedTitle", staged.Count)
            : _l.Format("studio.upload.stagedEmpty", Localizer.Isolate(_shell.ActiveChannel?.Name ?? ""));
        if (ReferenceEquals(staged, _renderedStaged)) return;
        _renderedStaged = staged;
        _staged.Children.Clear();
        foreach (var plan in staged)
        {
            var file = Path.GetFileName(plan.FilePath);
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBox { Text = plan.Title };
            AutomationProperties.SetName(title, _l.Format("studio.upload.titleFor", file));
            title.TextChanged += (_, _) => _vm.RenameStaged(plan.FilePath, title.Text);
            var meta = new TextBlock
            {
                Text = file + " · " + Formats.Bytes(plan.Size, CultureInfo.InvariantCulture) + (plan.PublishAt is { } at ? " · " + _l.Format("studio.upload.publishesAt", Formats.ToLocalInput(at)) : ""),
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(meta, 1);
            var remove = new Button { Content = new SymbolIcon(Symbol.Remove) };
            AutomationProperties.SetName(remove, _l.Format("studio.upload.removeStaged", file));
            ToolTipService.SetToolTip(remove, _l.Format("studio.upload.removeStaged", file));
            remove.Click += (_, _) => _vm.RemoveStaged(plan.FilePath);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(title);
            grid.Children.Add(meta);
            grid.Children.Add(remove);
            _staged.Children.Add(grid);
        }
    }

    private void RenderQueue()
    {
        var items = _vm.Items;
        _queueEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            seen.Add(item.Id);
            if (!_rows.TryGetValue(item.Id, out var row))
            {
                row = new QueueRow(this, item.Id);
                _rows[item.Id] = row;
                _queue.Children.Add(row.Root);
            }

            row.Update(item);
        }

        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _queue.Children.Remove(_rows[gone].Root);
            _rows.Remove(gone);
        }
    }

    private sealed class QueueRow
    {
        private readonly UploadPage _page;
        private readonly string _id;
        private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _status = new() { Opacity = 0.8 };
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        private UploadPhase? _phase;

        public QueueRow(UploadPage page, string id)
        {
            _page = page;
            _id = id;
            Root = new Grid { ColumnSpacing = 12 };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(_title);
            text.Children.Add(_status);
            Grid.SetColumn(_bar, 1);
            Grid.SetColumn(_buttons, 2);
            Root.Children.Add(text);
            Root.Children.Add(_bar);
            Root.Children.Add(_buttons);
        }

        public Grid Root { get; }

        public void Update(UploadSnapshot item)
        {
            var l = _page._l;
            _title.Text = item.Title;
            _bar.Value = item.Fraction;
            var phase = l["studio.phase." + item.Phase.ToString().ToLowerInvariant()];
            var amount = l.Format("studio.upload.progress", Formats.Bytes(item.SentBytes, CultureInfo.InvariantCulture), Formats.Bytes(item.TotalBytes, CultureInfo.InvariantCulture));
            _status.Text = item.Phase == UploadPhase.Uploading ? phase + " · " + amount : phase;
            AutomationProperties.SetName(_bar, item.Title + " · " + phase);
            if (_phase == item.Phase) return;
            _phase = item.Phase;
            _buttons.Children.Clear();
            var vm = _page._vm;
            switch (item.Phase)
            {
                case UploadPhase.Paused:
                    _buttons.Children.Add(Act(l["studio.upload.resume"], () => vm.Resume(_id)));
                    _buttons.Children.Add(Cancel(item));
                    break;
                case UploadPhase.Failed:
                    _buttons.Children.Add(Act(l["studio.upload.retry"], () => vm.Retry(_id)));
                    _buttons.Children.Add(Act(l["studio.upload.remove"], () => vm.Remove(_id)));
                    break;
                case UploadPhase.Done:
                case UploadPhase.Cancelled:
                    _buttons.Children.Add(Act(l["studio.upload.remove"], () => vm.Remove(_id)));
                    break;
                default:
                    _buttons.Children.Add(Act(l["studio.upload.pause"], () => vm.Pause(_id)));
                    _buttons.Children.Add(Cancel(item));
                    break;
            }
        }

        private Button Act(string text, Action a)
        {
            var b = new Button { Content = text };
            AutomationProperties.SetName(b, text + ": " + _title.Text);
            b.Click += (_, _) => a();
            return b;
        }

        private Button Cancel(UploadSnapshot item)
        {
            var l = _page._l;
            var b = new Button { Content = l["studio.upload.cancel"] };
            AutomationProperties.SetName(b, l["studio.upload.cancel"] + ": " + item.Title);
            b.Click += async (_, _) =>
            {
                if (_page._root() is not { } root) return;
                if (await Confirm.AskAsync(root, _page._direction, l.Format("studio.upload.cancelTitle", Localizer.Isolate(item.Title)), l["studio.upload.cancelBody"], l["studio.upload.cancelConfirm"], l["studio.upload.keepUploading"]))
                {
                    await _page._vm.CancelAsync(_id);
                }
            };
            return b;
        }
    }
}
