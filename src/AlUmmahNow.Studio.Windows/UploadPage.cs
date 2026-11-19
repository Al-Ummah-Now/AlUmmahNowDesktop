using System.Globalization;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;
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
    private readonly TextBlock _dropText = new();
    private readonly FrameworkElement _stagedCard;
    private readonly StackPanel _queue = new() { Spacing = 8 };
    // Rows with nothing to show collapse: an empty row or blank text still takes the page spacing.
    // The watched folder (wraps: a long path never pushes the card wider) and Stop watching.
    private readonly Grid _watchRow = new() { ColumnSpacing = 8 };
    private readonly StackPanel _stagedActions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
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
        // Lantern Glass: files and the queue on the left, "Set for all" on the right; card actions in each footer.
        Root = new Grid { ColumnSpacing = 20, Padding = new Thickness(16, 4, 16, 8) };
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        // Wider windows give the form more room; the 1024 minimum gives the files more.
        Root.SizeChanged += (_, e) => Root.ColumnDefinitions[1].Width = new GridLength(e.NewSize.Width >= 1500 ? 460 : e.NewSize.Width < 900 ? 340 : 400);
        var left = new StackPanel { Spacing = 18 };
        Root.Children.Add(left);

        // Add: the lantern, where the files go, and the ways to add them.
        // Two rows, so the actions fit the card down to the 1024 window: Add videos and Add a folder, then
        // Include subfolders and Watch folder.
        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addMore = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        add.Children.Add(Button(l["studio.upload.addFiles"], async () => _vm.AddFiles(await pickFiles()), accent: true, id: "AddFiles", glyph: "\uE710"));
        add.Children.Add(Button(l["studio.upload.addFolder"], async () =>
        {
            if (await pickFolder() is { } f) _vm.AddFolder(f, _recursive!.IsChecked == true);
        }, id: "AddFolder", glyph: "\uE8B7"));
        _recursive = new CheckBox { Content = l["studio.upload.includeSubfolders"], Margin = new Thickness(4, 0, 0, 0) };
        addMore.Children.Add(_recursive);
        addMore.Children.Add(Button(l["studio.upload.watch"], async () =>
        {
            if (await pickFolder() is not { } f) return;
            _vm.Watch(f);
            rememberWatchFolder(f);
        }, glyph: "\uE7B3"));
        var drop = new Grid { ColumnSpacing = 20, Padding = new Thickness(6), BorderBrush = LanternGlass.Res("StudioEdgeStrongBrush"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(12) };
        drop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        drop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        drop.Children.Add(Lantern());
        var dropText = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 14, 0, 14) };
        _dropText.TextWrapping = TextWrapping.Wrap;
        _dropText.FontSize = 15.5;
        _dropText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        dropText.Children.Add(_dropText);
        dropText.Children.Add(add);
        dropText.Children.Add(addMore);
        Grid.SetColumn(dropText, 1);
        drop.Children.Add(dropText);
        var addBody = new StackPanel { Spacing = 12 };
        addBody.Children.Add(drop);
        var watchRow = _watchRow;
        watchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        watchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        watchRow.Children.Add(_watching);
        _stopWatching = Button(l["studio.upload.stopWatching"], () =>
        {
            _vm.Watch(null);
            rememberWatchFolder(null);
            return Task.CompletedTask;
        });
        Grid.SetColumn(_stopWatching, 1);
        _stopWatching.VerticalAlignment = VerticalAlignment.Center;
        watchRow.Children.Add(_stopWatching);
        addBody.Children.Add(watchRow);
        addBody.Children.Add(_error);
        left.Children.Add(LanternGlass.Card(addBody));

        // Ready to upload: the staged files with their titles; Clear list and Upload in the footer.
        AutomationProperties.SetHeadingLevel(_stagedTitle, AutomationHeadingLevel.Level2);
        var stagedBody = new StackPanel { Spacing = 10 };
        stagedBody.Children.Add(_stagedTitle);
        stagedBody.Children.Add(_staged);
        var actions = _stagedActions;
        _clear = Button(l["studio.upload.clear"], () =>
        {
            _vm.ClearStaged();
            return Task.CompletedTask;
        });
        actions.Children.Add(_clear);
        AutomationProperties.SetAutomationId(_start, "StartUpload");
        _start.Click += (_, _) => _vm.Start();
        actions.Children.Add(_start);
        _stagedCard = LanternGlass.Card(stagedBody, actions);
        left.Children.Add(_stagedCard);

        // Uploads: the queue; Clear finished in the footer.
        var queueTitle = new TextBlock { Text = l["studio.upload.queue"], Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(queueTitle, AutomationHeadingLevel.Level2);
        var queueBody = new StackPanel { Spacing = 10 };
        queueBody.Children.Add(queueTitle);
        _queueEmpty = new TextBlock { Text = l["studio.upload.queueEmpty"], Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        queueBody.Children.Add(_queueEmpty);
        queueBody.Children.Add(_queue);
        var queueFooter = new StackPanel { Orientation = Orientation.Horizontal };
        queueFooter.Children.Add(Button(l["studio.upload.clearFinished"], () =>
        {
            _vm.ClearFinished();
            return Task.CompletedTask;
        }));
        left.Children.Add(LanternGlass.Card(queueBody, queueFooter));

        // Set for all.
        var forAllTitle = new TextBlock { Text = l["studio.upload.forAll"], Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(forAllTitle, AutomationHeadingLevel.Level2);
        var forAll = new StackPanel { Spacing = 12 };
        forAll.Children.Add(forAllTitle);
        forAll.Children.Add(BuildDefaults());
        var right = LanternGlass.Card(forAll);
        right.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(right, 1);
        Root.Children.Add(right);

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

    public Grid Root { get; }

    private ComboBox? _visibility;

    /// <summary>CI screenshots: "scheduled" picks Scheduled, showing the schedule fields.</summary>
    public void RunScene(string scene)
    {
        if (scene == "scheduled" && _visibility != null) _visibility.SelectedIndex = 2;
    }

    private UploadDefaults D => _vm.Defaults;

    /// <summary>The upload lantern: a gold arrow in a glowing gold ring.</summary>
    private static FrameworkElement Lantern()
    {
        var ring = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(36),
            BorderThickness = new Thickness(2),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(LanternGlass.GoldEdge),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0x33, 0xF4, 0xAE, 0x3A)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Child = new FontIcon { Glyph = "\uE898", FontSize = 30, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(LanternGlass.Gold) },
        };
        return LanternGlass.WithGlow(ring, 36, 0.55f, 30f);
    }

    /// <summary>The title-pattern hint; its {n} examples use the UI digits (the token itself writes 0–9).</summary>
    private static string TitlePatternHint(Localizer l) => l.Format("studio.upload.titlePatternHint", 1, 2, 3);

    private static Button Button(string text, Func<Task> click, bool accent = false, string? id = null, string? glyph = null)
    {
        var b = new Button { Content = text };
        if (glyph != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
            row.Children.Add(new TextBlock { Text = text });
            b.Content = row;
            AutomationProperties.SetName(b, text);
        }

        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (id != null) AutomationProperties.SetAutomationId(b, id);
        b.Click += async (_, _) => await click();
        return b;
    }

    private FrameworkElement BuildDefaults()
    {
        var panel = new StackPanel { Spacing = 12 };

        var title = new TextBox { Header = _l["studio.upload.titlePattern"], Text = D.TitlePattern, Description = TitlePatternHint(_l) };
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

        var row = new Grid { ColumnSpacing = 10, RowSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
        var visibility = _visibility = new ComboBox { Header = _l["studio.upload.visibility"], ItemsSource = visibilities.Select(v => _l["studio.upload.visibility." + v.ToString().ToLowerInvariant()]).ToList(), SelectedIndex = 0, MinWidth = 160 };
        visibility.SelectionChanged += (_, _) =>
        {
            D.Visibility = visibilities[Math.Max(0, visibility.SelectedIndex)];
            _scheduleRow.Visibility = D.Visibility == UploadVisibility.Scheduled ? Visibility.Visible : Visibility.Collapsed;
            _vm.ApplyDefaults();
        };
        category.HorizontalAlignment = language.HorizontalAlignment = visibility.HorizontalAlignment = HorizontalAlignment.Stretch;
        category.MinWidth = language.MinWidth = visibility.MinWidth = 0;
        Grid.SetColumn(language, 1);
        Grid.SetRow(visibility, 1);
        Grid.SetColumnSpan(visibility, 2);
        row.Children.Add(category);
        row.Children.Add(language);
        row.Children.Add(visibility);
        panel.Children.Add(row);

        D.TimeZone = Formats.LocalTimeZone();
        D.ScheduleStart ??= DateTimeOffset.Now.Date.AddDays(1).AddHours(9);
        var start = new TextBox { Header = _l["studio.upload.scheduleStart"], Text = _l.InputDigits(Formats.ToLocalInput(D.ScheduleStart.Value)), MinWidth = 220 };
        start.TextChanged += (_, _) =>
        {
            var t = Formats.ParseLocal(Localizer.NormalizeDigits(start.Text));
            start.Description = t == null ? _l["studio.upload.scheduleInvalid"] : null;
            if (t == null) return;
            D.ScheduleStart = t;
            _vm.ApplyDefaults();
        };
        var every = new NumberBox { Header = _l["studio.upload.scheduleEvery"], Value = D.ScheduleInterval.TotalHours, Minimum = 0, Maximum = 24 * 30, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1 };
        // The hours in the UI's digits: ٠–٩ in Arabic (the formatter also reads typed ٠–٩ back).
        if (_l.Language == "ar") every.NumberFormatter = new DecimalFormatter { NumeralSystem = "Arab", FractionDigits = 0, IntegerDigits = 1, IsGrouped = false };
        every.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(every.Value)) return;
            D.ScheduleInterval = TimeSpan.FromHours(every.Value);
            _vm.ApplyDefaults();
        };
        // "Every … hours" across a batch is bulk scheduling: hidden when bulkSchedule is off.
        every.Visibility = _vm.BulkScheduleOn ? Visibility.Visible : Visibility.Collapsed;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UploadsViewModel.BulkScheduleOn)) every.Visibility = _vm.BulkScheduleOn ? Visibility.Visible : Visibility.Collapsed;
        };
        var scheduleFields = new StackPanel { Spacing = 12 };
        start.MinWidth = 0;
        start.HorizontalAlignment = every.HorizontalAlignment = HorizontalAlignment.Stretch;
        scheduleFields.Children.Add(start);
        scheduleFields.Children.Add(every);
        _scheduleRow.Children.Add(scheduleFields);
        panel.Children.Add(_scheduleRow);

        var playlistsHeader = new TextBlock { Text = _l["studio.upload.playlists"] };
        panel.Children.Add(playlistsHeader);
        panel.Children.Add(_playlists);
        var newPlaylist = new TextBox { PlaceholderText = _l["studio.upload.newPlaylist"] };
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
        var createRow = new Grid { ColumnSpacing = 8 };
        createRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        createRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(create, 1);
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
        _watchRow.Visibility = _stopWatching.Visibility;
    }

    private void RenderStaged()
    {
        var staged = _vm.Staged;
        _start.Content = staged.Count == 1 ? _l["studio.upload.startOne"] : _l.Format("studio.upload.start", staged.Count);
        _start.IsEnabled = _vm.CanStart;
        _stagedCard.Visibility = staged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _stagedTitle.Text = _l.Format("studio.upload.stagedTitle", staged.Count);
        _dropText.Text = _l.Format("studio.upload.stagedEmpty", Localizer.Isolate(_shell.ActiveChannel?.Name ?? ""));
        if (ReferenceEquals(staged, _renderedStaged)) return;
        _renderedStaged = staged;
        _staged.Children.Clear();
        foreach (var plan in staged)
        {
            var file = Path.GetFileName(plan.FilePath);
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBox { Text = plan.Title };
            AutomationProperties.SetName(title, _l.Format("studio.upload.titleFor", file));
            title.TextChanged += (_, _) => _vm.RenameStaged(plan.FilePath, title.Text);
            var meta = new TextBlock
            {
                Text = _l.Join(Localizer.Isolate(file), _l.Bytes(plan.Size, 2), plan.PublishAt is { } at ? _l.Format("studio.upload.publishesAt", _l.DateTime(at)) : null),
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(title);
            text.Children.Add(meta);
            Grid.SetColumn(text, 1);
            var remove = new Button { Content = new SymbolIcon(Symbol.Cancel), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, _l.Format("studio.upload.removeStaged", file));
            ToolTipService.SetToolTip(remove, _l.Format("studio.upload.removeStaged", file));
            remove.Click += (_, _) => _vm.RemoveStaged(plan.FilePath);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(FileTile(96, 54));
            grid.Children.Add(text);
            grid.Children.Add(remove);
            _staged.Children.Add(grid);
        }
    }

    /// <summary>A video file's tile: night blue with a gold film glyph (the server makes the real thumbnail).</summary>
    private static Border FileTile(double width, double height) => new()
    {
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(8),
        VerticalAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(1),
        BorderBrush = LanternGlass.Res("StudioEdgeBrush"),
        Background = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new Microsoft.UI.Xaml.Media.GradientStop { Color = global::Windows.UI.Color.FromArgb(0xFF, 0x18, 0x44, 0x56), Offset = 0 },
                new Microsoft.UI.Xaml.Media.GradientStop { Color = LanternGlass.Night, Offset = 1 },
            },
        },
        Child = new FontIcon { Glyph = "\uE8B2", FontSize = height * 0.4, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(LanternGlass.Gold) },
    };

    private void RenderQueue()
    {
        var items = _vm.Items;
        _queueEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _queue.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            Root = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var text = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(_title);
            text.Children.Add(_bar);
            text.Children.Add(_status);
            Grid.SetColumn(text, 1);
            _buttons.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_buttons, 2);
            Root.Children.Add(FileTile(80, 45));
            Root.Children.Add(text);
            Root.Children.Add(_buttons);
        }

        public Grid Root { get; }

        public void Update(UploadSnapshot item)
        {
            var l = _page._l;
            _title.Text = item.Title;
            _bar.Value = item.Fraction;
            var phase = l["studio.phase." + item.Phase.ToString().ToLowerInvariant()];
            var amount = l.Format("studio.upload.progress", l.Bytes(item.SentBytes, 2), l.Bytes(item.TotalBytes, 2));
            _status.Text = item.Phase == UploadPhase.Uploading ? l.Join(phase, amount) : phase;
            AutomationProperties.SetName(_bar, l.Join(Localizer.Isolate(item.Title), phase));
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
