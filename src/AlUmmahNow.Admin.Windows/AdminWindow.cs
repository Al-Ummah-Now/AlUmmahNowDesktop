using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using AlUmmahNow.Admin.Auth;
using AlUmmahNow.Admin.Media;
using AlUmmahNow.Admin.Review;
using AlUmmahNow.Admin.Security;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace AlUmmahNow.Admin.WindowsApp;

internal enum AdminState { Locked, SignedOut, SigningIn, Checking, NotStaff, Ready }

/// <summary>Everything the window needs from the app (built once in App).</summary>
internal sealed class AdminContext
{
    public required Localizer L { get; init; }
    public required AdminSettings Settings { get; init; }
    public required Action SaveSettings { get; init; }
    public required IAdminAuth Auth { get; init; }
    public required IReviewService Service { get; init; }
    public required LockPolicy Lock { get; init; }
    public required RevealPolicy Reveal { get; init; }
    public required string InstallId { get; init; }
    public required bool Demo { get; init; }
    public string? Banner { get; init; }
    public string? PushMessage { get; set; }
    public Func<Task>? AfterSignIn { get; init; }
    public Func<Task>? BeforeSignOut { get; init; }
}

/// <summary>
/// The Admin window: lock, sign-in and "staff only" states, then the review queue beside the
/// open review. Built in code like Studio's window; rebuilt when the language changes.
/// </summary>
internal sealed class AdminWindow
{
    private static readonly Logger Logger = Log.For("ui");

    private readonly AdminContext _c;
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _second;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly Grid _root = new();
    private readonly Grid _body = new();
    private readonly InfoBar _toast = new() { IsClosable = true, Margin = new Thickness(12) };
    private readonly ReviewQueue _queue;
    private readonly ReviewSession _session;
    private StreamPlayer? _player;
    private AdminState _state = AdminState.Locked;
    private StaffProfile? _profile;
    private bool _hidden;
    private bool _minimised;
    private string? _signInError;

    // Built per language.
    private ListView _list = new();
    private AutoSuggestBox _search = new();
    private SelectorBar _tabs = new();
    private InfoBar _offline = new();
    private InfoBar _push = new();
    private ScrollViewer _detailScroll = new();
    private StackPanel _detail = new();
    private StackPanel _media = new();
    private StackPanel _frames = new();
    private InfoBar _detailInfo = new();
    private readonly Dictionary<ReviewDecision, Button> _decisionButtons = new();
    private bool _renderingList;

    public AdminWindow(AdminContext context)
    {
        _c = context;
        _ui = DispatcherQueue.GetForCurrentThread();
        _queue = new ReviewQueue(context.Service);
        _session = new ReviewSession(context.Service, _queue, context.Reveal, context.InstallId, StepUpAsync);
        Window = new Window { Title = context.L["admin.name"], Content = _root };
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);
        Protection.ExcludeFromCapture(Hwnd);

        _queue.PropertyChanged += (_, e) => _ui.TryEnqueue(() => OnQueueChanged(e));
        _session.PropertyChanged += (_, e) => _ui.TryEnqueue(() => OnSessionChanged(e));
        context.Reveal.Changed += (_, _) => _ui.TryEnqueue(RenderMedia);
        context.Lock.Changed += (_, _) => _ui.TryEnqueue(() =>
        {
            if (_c.Lock.IsLocked) SetState(AdminState.Locked);
        });

        _second = _ui.CreateTimer();
        _second.Interval = TimeSpan.FromSeconds(1);
        _second.Tick += (_, _) =>
        {
            _c.Reveal.Tick();
            _session.ExpireUndo();
        };
        _second.Start();
        _toastTimer = _ui.CreateTimer();
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => _toast.IsOpen = false;

        // Any input counts against the 60 s idle re-blur.
        _root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) => _c.Reveal.Input()), true);
        _root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);

        Window.AppWindow.Changed += (sender, e) =>
        {
            if (!e.DidPresenterChange && !e.DidSizeChange && !e.DidVisibilityChange) return;
            var minimised = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } || !sender.IsVisible;
            if (minimised != _minimised)
            {
                _minimised = minimised;
                if (minimised) OnHidden();
                else OnShown();
            }
        };
        Window.Activated += (_, e) => _c.Reveal.Input();
        Build();
    }

    public Window Window { get; }

    public IntPtr Hwnd { get; }

    public AdminState State => _state;

    public ReviewQueue Queue => _queue;

    public ReviewSession Session => _session;

    public StaffProfile? Profile => _profile;

    public string LastToast { get; private set; } = "";

    private Localizer L => _c.L;

    // ---------------- state ----------------

    public async Task StartAsync()
    {
        SetState(AdminState.Locked);
        await UnlockAsync();
    }

    private void SetState(AdminState state)
    {
        _state = state;
        Render();
    }

    public async Task UnlockAsync()
    {
        if (!_c.Lock.IsLocked)
        {
            await AfterUnlockAsync();
            return;
        }

        var result = _c.Demo && Environment.GetEnvironmentVariable("AUN_DEMO_HELLO") != "ask" ? HelloResult.Verified : await Hello.VerifyAsync(Hwnd, L["admin.lock.prompt"]);
        if (result != HelloResult.Verified)
        {
            _signInError = result == HelloResult.NotAvailable ? L["admin.lock.unavailable"] : L["admin.lock.failed"];
            Render();
            return;
        }

        _signInError = null;
        _c.Lock.Unlocked();
        await AfterUnlockAsync();
    }

    private async Task AfterUnlockAsync()
    {
        if (!_c.Auth.IsSignedIn)
        {
            SetState(AdminState.SignedOut);
            return;
        }

        if (_profile != null)
        {
            SetState(AdminState.Ready);
            return;
        }

        SetState(AdminState.Checking);
        try
        {
            _profile = await _c.Service.ProfileAsync();
            SetState(AdminState.Ready);
            await _queue.RefreshAsync();
            if (_c.AfterSignIn != null) await _c.AfterSignIn();
        }
        catch (NotStaffException)
        {
            SetState(AdminState.NotStaff);
        }
        catch (SignInRequiredException)
        {
            SetState(AdminState.SignedOut);
        }
        catch (OfflineException)
        {
            // The text list may still be shown from the last session; decisions stay off.
            _profile = new StaffProfile(_c.Auth.UserId ?? "", _c.Auth.DisplayName, Array.Empty<string>());
            SetState(AdminState.Ready);
            await _queue.RefreshAsync();
        }
        catch (ReviewException ex)
        {
            _signInError = L["admin.error.generic"];
            Logger.Warn("profile check failed: " + ex.GetType().Name);
            SetState(AdminState.SignedOut);
        }
    }

    private async Task SignInAsync()
    {
        _signInError = null;
        SetState(AdminState.SigningIn);
        try
        {
            await _c.Auth.SignInAsync();
            _profile = null;
            await AfterUnlockAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn("sign-in failed: " + ex.GetType().Name);
            _signInError = L["admin.signin.failed"];
            SetState(AdminState.SignedOut);
        }
    }

    public async Task SignOutAsync(bool ask = true)
    {
        if (ask && !await Dialogs.ConfirmAsync(_root.XamlRoot, Direction, L["admin.signout.confirm.title"], L["admin.signout.confirm.body"], L["admin.signout"], L["admin.cancel"])) return;
        _session.Close();
        _player?.Stop();
        if (_c.BeforeSignOut != null) await _c.BeforeSignOut();
        await _c.Auth.SignOutAsync();
        _profile = null;
        SetState(AdminState.SignedOut);
    }

    private async Task<bool> StepUpAsync(CancellationToken ct)
    {
        var go = await Dialogs.ConfirmAsync(_root.XamlRoot, Direction, L["admin.stepup.title"], L["admin.stepup.body"], L["admin.stepup.continue"], L["admin.cancel"]);
        return go && await _c.Auth.StepUpAsync(ct);
    }

    /// <summary>Minimised, hidden or the session locked: nothing of the queue stays on screen.</summary>
    public void OnHidden()
    {
        if (_hidden) return;
        _hidden = true;
        _c.Lock.Hidden();
        _c.Reveal.Hide();
        _player?.Pause();
        Render();
    }

    public void OnShown()
    {
        if (!_hidden) return;
        _hidden = false;
        if (_c.Lock.Shown())
        {
            SetState(AdminState.Locked);
            _ = UnlockAsync();
        }
        else
        {
            Render();
        }
    }

    // ---------------- build ----------------

    private FlowDirection Direction => L.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>(Re)builds every control for the current language and theme.</summary>
    public void Build()
    {
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.FlowDirection = Direction;
        _root.RequestedTheme = _c.Settings.Theme == "dark" ? ElementTheme.Dark : ElementTheme.Light;
        _root.Background = new SolidColorBrush(_c.Settings.Theme == "dark" ? Color(0x20, 0x20, 0x20) : Color(0xF7, 0xF7, 0xF7));
        Window.Title = L["admin.name"];
        foreach (var _ in Enumerable.Range(0, 4)) _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);

        if (!string.IsNullOrEmpty(_c.Banner)) _root.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = _c.Banner });
        var bar = TopBar();
        Grid.SetRow(bar, 1);
        _root.Children.Add(bar);
        Grid.SetRow(_body, 2);
        _root.Children.Add(_body);
        Grid.SetRow(_toast, 3);
        _root.Children.Add(_toast);
        AutomationProperties.SetAutomationId(_toast, "Toast");
        AutomationProperties.SetLiveSetting(_toast, AutomationLiveSetting.Assertive);
        BuildQueue();
        BuildDetail();
        Render();
    }

    private FrameworkElement TopBar()
    {
        var grid = new Grid { Padding = new Thickness(16, 8, 16, 8), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = L["admin.name"], Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        grid.Children.Add(title);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var theme = new ComboBox { MinWidth = 110, Header = null };
        theme.Items.Add(L["admin.settings.light"]);
        theme.Items.Add(L["admin.settings.dark"]);
        theme.SelectedIndex = _c.Settings.Theme == "dark" ? 1 : 0;
        AutomationProperties.SetName(theme, L["admin.settings.theme"]);
        AutomationProperties.SetAutomationId(theme, "Theme");
        theme.SelectionChanged += (_, _) =>
        {
            var value = theme.SelectedIndex == 1 ? "dark" : "light";
            if (value == _c.Settings.Theme) return;
            _c.Settings.Theme = value;
            _c.SaveSettings();
            _ui.TryEnqueue(Build);
        };
        var language = new ComboBox { MinWidth = 110 };
        language.Items.Add("English");
        language.Items.Add("العربية");
        language.SelectedIndex = L.Language == "ar" ? 1 : 0;
        AutomationProperties.SetName(language, L["admin.settings.language"]);
        AutomationProperties.SetAutomationId(language, "Language");
        language.SelectionChanged += (_, _) =>
        {
            var value = language.SelectedIndex == 1 ? "ar" : "en";
            if (value == L.Language) return;
            L.Language = value;
            _c.Settings.Language = value;
            _c.SaveSettings();
            _ui.TryEnqueue(Build);
        };
        tools.Children.Add(theme);
        tools.Children.Add(language);
        if (_c.Auth.IsSignedIn && _state != AdminState.Locked)
        {
            var menu = new MenuFlyout();
            var signOut = new MenuFlyoutItem { Text = L["admin.signout"] };
            signOut.Click += (_, _) => _ = SignOutAsync();
            menu.Items.Add(signOut);
            var account = new DropDownButton { Content = _c.Auth.DisplayName ?? "", Flyout = menu };
            AutomationProperties.SetName(account, L.Format("admin.account", _c.Auth.DisplayName ?? ""));
            AutomationProperties.SetAutomationId(account, "AccountMenu");
            tools.Children.Add(account);
        }

        Grid.SetColumn(tools, 1);
        grid.Children.Add(tools);
        return grid;
    }

    private void BuildQueue()
    {
        _tabs = new SelectorBar();
        var action = new SelectorBarItem { Text = L["admin.tab.action"], Tag = ReviewTab.ActionNeeded };
        var history = new SelectorBarItem { Text = L["admin.tab.history"], Tag = ReviewTab.History };
        AutomationProperties.SetAutomationId(action, "TabActionNeeded");
        AutomationProperties.SetAutomationId(history, "TabHistory");
        _tabs.Items.Add(action);
        _tabs.Items.Add(history);
        _tabs.SelectedItem = _queue.Tab == ReviewTab.History ? history : action;
        _tabs.SelectionChanged += (s, _) =>
        {
            if (s.SelectedItem?.Tag is ReviewTab tab) _queue.Tab = tab;
        };

        _search = new AutoSuggestBox { PlaceholderText = L["admin.search"], QueryIcon = new SymbolIcon(Symbol.Find), Text = _queue.Query };
        AutomationProperties.SetName(_search, L["admin.search"]);
        AutomationProperties.SetAutomationId(_search, "QueueSearch");
        _search.TextChanged += (s, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            s.ItemsSource = _queue.Suggestions(s.Text);
            _ = _queue.SearchAsync(s.Text);
        };
        _search.SuggestionChosen += (s, e) => s.Text = e.SelectedItem as string ?? s.Text;
        _search.QuerySubmitted += (s, e) => _ = _queue.SearchAsync(e.ChosenSuggestion as string ?? e.QueryText);

        _offline = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, Message = L["admin.offline"] };
        AutomationProperties.SetAutomationId(_offline, "Offline");
        _push = new InfoBar { Severity = InfoBarSeverity.Informational, IsClosable = true, Message = _c.PushMessage ?? "" };
        AutomationProperties.SetAutomationId(_push, "PushStatus");

        _list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, TabIndex = 1 };
        AutomationProperties.SetName(_list, L["admin.tab.action"]);
        AutomationProperties.SetAutomationId(_list, "QueueList");
        _list.SelectionChanged += (_, _) =>
        {
            if (_renderingList) return;
            if (_list.SelectedItem is FrameworkElement { Tag: string id } && _session.Detail?.Item.Id != id) _ = OpenAsync(id);
        };
    }

    private void BuildDetail()
    {
        _detail = new StackPanel { Spacing = 12, Padding = new Thickness(16) };
        _detailScroll = new ScrollViewer { Content = _detail, HorizontalScrollMode = ScrollMode.Disabled };
        _media = new StackPanel { Spacing = 8 };
        _frames = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _detailInfo = new InfoBar { IsClosable = false };
        AutomationProperties.SetAutomationId(_detailInfo, "DetailInfo");
        _player?.Stop();
        _player = new StreamPlayer(L);
    }

    // ---------------- render ----------------

    private void Render()
    {
        _body.Children.Clear();
        _body.ColumnDefinitions.Clear();
        if (_hidden)
        {
            _body.Children.Add(Centered(L["admin.hidden"], null, null));
            return;
        }

        switch (_state)
        {
            case AdminState.Locked:
                _body.Children.Add(Centered(L["admin.lock.title"], _signInError ?? L["admin.lock.body"], (L["admin.lock.unlock"], "Unlock", () => _ = UnlockAsync())));
                break;
            case AdminState.SignedOut:
                _body.Children.Add(Centered(L["admin.signin.title"], _signInError ?? L["admin.signin.body"], (L["admin.signin.button"], "SignIn", () => _ = SignInAsync())));
                break;
            case AdminState.SigningIn:
            case AdminState.Checking:
                _body.Children.Add(Centered(L["admin.signin.title"], L["admin.signin.waiting"], null, busy: true));
                break;
            case AdminState.NotStaff:
                _body.Children.Add(Centered(L["admin.notstaff.title"], L.Format("admin.notstaff.body", _c.Auth.Email ?? _c.Auth.DisplayName ?? ""), (L["admin.signout"], "SignOut", () => _ = SignOutAsync(ask: false))));
                break;
            default:
                RenderReady();
                break;
        }
    }

    private FrameworkElement Centered(string title, string? body, (string Label, string Id, Action Click)? action, bool busy = false)
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(24) };
        var heading = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        panel.Children.Add(heading);
        if (body != null)
        {
            var text = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
            panel.Children.Add(text);
        }

        if (busy) panel.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        if (action is { } a)
        {
            var button = new Button { Content = a.Label, HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 160 };
            AutomationProperties.SetAutomationId(button, a.Id);
            AutomationProperties.SetName(button, a.Label);
            button.Click += (_, _) => a.Click();
            panel.Children.Add(button);
            button.Loaded += (_, _) => button.Focus(FocusState.Programmatic);
        }

        return panel;
    }

    private void RenderReady()
    {
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380), MinWidth = 300 });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var queue = new Grid { Padding = new Thickness(12, 0, 12, 12), RowSpacing = 8 };
        foreach (var _ in Enumerable.Range(0, 5)) queue.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        queue.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star);
        queue.Children.Add(_tabs);
        Grid.SetRow(_search, 1);
        queue.Children.Add(_search);
        Grid.SetRow(_offline, 2);
        queue.Children.Add(_offline);
        Grid.SetRow(_push, 3);
        queue.Children.Add(_push);
        Grid.SetRow(_list, 4);
        queue.Children.Add(_list);
        _body.Children.Add(queue);

        Grid.SetColumn(_detailScroll, 1);
        _body.Children.Add(_detailScroll);
        _push.IsOpen = !string.IsNullOrEmpty(_c.PushMessage);
        _push.Message = _c.PushMessage ?? "";
        RenderList();
        RenderDetail();
    }

    private void OnQueueChanged(PropertyChangedEventArgs e)
    {
        if (_state != AdminState.Ready) return;
        RenderList();
        if (e.PropertyName == nameof(ReviewQueue.IsOffline)) RenderDecisionBar();
    }

    private void OnSessionChanged(PropertyChangedEventArgs e)
    {
        if (_state != AdminState.Ready) return;
        switch (e.PropertyName)
        {
            case nameof(ReviewSession.Detail):
            case nameof(ReviewSession.IsLoading):
            case nameof(ReviewSession.LoadError):
                RenderDetail();
                break;
            case nameof(ReviewSession.ClaimError):
            case nameof(ReviewSession.IsBusy):
            case nameof(ReviewSession.CanDecide):
                RenderInfo();
                RenderDecisionBar();
                break;
        }
    }

    private void RenderList()
    {
        _offline.IsOpen = _queue.IsOffline;
        _renderingList = true;
        try
        {
            _list.Items.Clear();
            AutomationProperties.SetName(_list, _queue.Tab == ReviewTab.History ? L["admin.tab.history"] : L["admin.tab.action"]);
            foreach (var item in _queue.Items) _list.Items.Add(Row(item));
            if (_queue.Items.Count == 0)
            {
                var empty = string.IsNullOrWhiteSpace(_queue.Query)
                    ? (_queue.Tab == ReviewTab.History ? L["admin.queue.empty.history"] : L["admin.queue.empty"])
                    : L.Format("admin.queue.nomatch", _queue.Query);
                var t = new TextBlock { Text = empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), IsTabStop = false };
                AutomationProperties.SetLiveSetting(t, AutomationLiveSetting.Polite);
                _list.Items.Add(new ListViewItem { Content = t, IsEnabled = false });
            }

            if (_queue.HasMore)
            {
                var more = new Button { Content = L["admin.queue.more"], HorizontalAlignment = HorizontalAlignment.Stretch };
                more.Click += (_, _) => _ = _queue.LoadMoreAsync();
                _list.Items.Add(new ListViewItem { Content = more });
            }

            var open = _session.Detail?.Item.Id;
            _list.SelectedItem = _list.Items.OfType<FrameworkElement>().FirstOrDefault(x => x.Tag as string == open);
        }
        finally
        {
            _renderingList = false;
        }
    }

    private FrameworkElement Row(ReviewItem item)
    {
        var panel = new StackPanel { Spacing = 2, Padding = new Thickness(4, 8, 4, 8) };
        var title = new TextBlock { Text = item.Title ?? item.SubjectId, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
        panel.Children.Add(title);
        var meta = string.Join(" · ", new[] { item.ChannelName, Cause(item), Deadline(item), item.Risk is { } r ? L.Format("admin.row.risk", L.Number((long)Math.Round(r * 100)) + "%") : null }.Where(s => !string.IsNullOrEmpty(s)));
        panel.Children.Add(new TextBlock { Text = meta, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        var flags = Flags(item);
        if (flags.Count > 0)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var f in flags) chips.Children.Add(Chip(f));
            panel.Children.Add(chips);
        }

        if (!item.IsOpen && item.Decision != null)
            panel.Children.Add(new TextBlock { Text = L.Format("admin.row.decided", DecidedWord(item.Decision), item.DecidedByName ?? ""), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        else if (item.AssignedToName is { } holder && item.AssignedTo != _profile?.UserId)
            panel.Children.Add(new TextBlock { Text = L.Format("admin.row.claimed", holder), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });

        if (item.IsOpen && item.SlaDueAt is { } due && due <= DateTimeOffset.UtcNow)
            panel.BorderBrush = new SolidColorBrush(Colors.IndianRed);
        panel.BorderThickness = new Thickness(L.IsRtl ? 0 : 3, 0, L.IsRtl ? 3 : 0, 0);
        panel.BorderBrush ??= new SolidColorBrush(Colors.Transparent);

        var row = new ListViewItem { Content = panel, Tag = item.Id };
        AutomationProperties.SetName(row, string.Join(", ", new[] { item.Title, meta }.Concat(flags).Where(s => !string.IsNullOrEmpty(s))));
        AutomationProperties.SetAutomationId(row, "Row-" + item.Id);
        return row;
    }

    private List<string> Flags(ReviewItem item)
    {
        var flags = new List<string>();
        if (item.Minor) flags.Add(L["admin.row.minor"]);
        else if (item.IsSensitive) flags.Add(L["admin.row.sensitive"]);
        if (item.Escalate) flags.Add(L["admin.row.escalated"]);
        return flags;
    }

    private static Border Chip(string text) => new()
    {
        Child = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
        Padding = new Thickness(6, 1, 6, 1),
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
    };

    private string? Cause(ReviewItem item) => item.HoldCause is { } c ? L["admin.cause." + Wire.Of(c)] : null;

    private string Deadline(ReviewItem item)
    {
        if (!item.IsOpen) return "";
        if (item.SlaDueAt is not { } due) return L["admin.row.nodue"];
        var left = due - DateTimeOffset.UtcNow;
        var span = Span(left.Duration());
        return left < TimeSpan.Zero ? L.Format("admin.row.overdue", span) : L.Format("admin.row.due", span);
    }

    private string Span(TimeSpan t) => t.TotalMinutes < 90
        ? L.Format("admin.minutes", L.Number((long)Math.Max(1, Math.Round(t.TotalMinutes))))
        : L.Format("admin.hours", L.Number((long)Math.Round(t.TotalHours)));

    private string DecidedWord(string decision) => L["admin.decided." + decision] is var w && !w.StartsWith("admin.", StringComparison.Ordinal) ? w : decision;

    private void RenderDetail()
    {
        _detail.Children.Clear();
        _decisionButtons.Clear();
        if (_session.IsLoading)
        {
            _detail.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center });
            _detail.Children.Add(new TextBlock { Text = L["admin.detail.loading"], HorizontalAlignment = HorizontalAlignment.Center });
            return;
        }

        if (_session.LoadError != null)
        {
            _detail.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = ErrorText(_session.LoadError) });
            return;
        }

        var detail = _session.Detail;
        if (detail == null)
        {
            _player?.Stop();
            _detail.Children.Add(new TextBlock { Text = L["admin.detail.empty"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 48, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            return;
        }

        var item = detail.Item;
        var heading = new TextBlock { Text = item.Title ?? detail.Subject?.Title ?? item.SubjectId, Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        AutomationProperties.SetAutomationId(heading, "DetailTitle");
        _detail.Children.Add(heading);
        var meta = string.Join(" · ", new[] { item.ChannelName, item.ChannelTrust is { } t ? L.Format("admin.detail.trust", t) : null, Cause(item), Deadline(item) }.Where(s => !string.IsNullOrEmpty(s)));
        _detail.Children.Add(new TextBlock { Text = meta, TextWrapping = TextWrapping.Wrap });

        _detail.Children.Add(_detailInfo);
        RenderInfo();
        _detail.Children.Add(DecisionBar());

        _detail.Children.Add(_media);
        _ = _player!.LoadAsync(item.IsOpen ? detail.Stream : null);
        RenderMedia();

        _detail.Children.Add(Section("admin.detail.frames"));
        _detail.Children.Add(new ScrollViewer { Content = _frames, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled });
        _ = RenderFramesAsync(detail);

        _detail.Children.Add(Section("admin.detail.findings"));
        foreach (var f in detail.Findings.OrderByDescending(f => f.Score))
        {
            var label = L.Format("admin.detail.score", ReasonLabel(f.Category), L.Decimal(Math.Round(f.Score, 2)));
            if (f.AtMs is { } at) label += " · " + L.Format("admin.detail.at", StreamPlayer.Clock(TimeSpan.FromMilliseconds(at)));
            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            foreach (var extra in new[] { f.Rationale, f.Text, f.QuranRef }.Where(s => !string.IsNullOrWhiteSpace(s)))
                block.Children.Add(new TextBlock { Text = extra, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false });
            _detail.Children.Add(block);
        }

        _detail.Children.Add(Section("admin.detail.metadata"));
        if (detail.Subject is { } s)
        {
            _detail.Children.Add(new TextBlock { Text = s.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(s.Description)) _detail.Children.Add(new TextBlock { Text = s.Description, TextWrapping = TextWrapping.Wrap });
            if (s.Tags.Count > 0) _detail.Children.Add(new TextBlock { Text = string.Join(" · ", s.Tags.Select(x => "#" + x)), TextWrapping = TextWrapping.Wrap });
        }

        _detail.Children.Add(Section("admin.detail.history"));
        if (detail.History.Count == 0) _detail.Children.Add(new TextBlock { Text = L["admin.detail.nohistory"] });
        foreach (var h in detail.History)
        {
            var word = h.Decision != null ? DecidedWord(h.Decision) : h.Status ?? "";
            var parts = new[] { word, h.ReasonCode != null ? ReasonLabel(h.ReasonCode) : null, h.DecidedByName, h.DecidedAt?.ToLocalTime().ToString("d", L.Language == "ar" ? new System.Globalization.CultureInfo("ar") : System.Globalization.CultureInfo.InvariantCulture) };
            _detail.Children.Add(new TextBlock { Text = string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p))), TextWrapping = TextWrapping.Wrap });
        }

        _detailScroll.ChangeView(null, 0, null, true);
    }

    private TextBlock Section(string key)
    {
        var t = new TextBlock { Text = L[key], Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level3);
        return t;
    }

    private string ReasonLabel(string code) => L["admin.reason." + code] is var r && !r.StartsWith("admin.", StringComparison.Ordinal) ? r : code;

    private void RenderInfo()
    {
        var detail = _session.Detail;
        if (detail == null) return;
        string? text = null;
        var severity = InfoBarSeverity.Informational;
        if (!detail.Item.IsOpen && detail.Item.Decision != null)
        {
            text = L.Format("admin.detail.decided", L.Format("admin.row.decided", DecidedWord(detail.Item.Decision), detail.Item.DecidedByName ?? ""));
        }
        else if (_session.ClaimError is ClaimedByOtherException c)
        {
            text = L.Format("admin.detail.claimedby", c.Name ?? L["admin.conflict.someone"]);
            severity = InfoBarSeverity.Warning;
        }
        else if (_session.ClaimError != null)
        {
            text = L["admin.detail.claimfailed"];
            severity = InfoBarSeverity.Warning;
        }

        _detailInfo.Message = text ?? "";
        _detailInfo.Severity = severity;
        _detailInfo.IsOpen = text != null;
    }

    private FrameworkElement DecisionBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Add(ReviewDecision d, string key, string shortcut, string id, bool accent = false)
        {
            var b = new Button { Content = L[key] + "  (" + shortcut + ")", MinHeight = 36 };
            if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            AutomationProperties.SetName(b, L[key]);
            AutomationProperties.SetAcceleratorKey(b, shortcut);
            AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => _ = DecideAsync(d);
            _decisionButtons[d] = b;
            bar.Children.Add(b);
        }

        Add(ReviewDecision.Approve, "admin.decision.approve", "A", "Approve", accent: true);
        Add(ReviewDecision.Reject, "admin.decision.reject", "R", "Reject");
        Add(ReviewDecision.RequestChanges, "admin.decision.request_changes", "C", "RequestChanges");
        Add(ReviewDecision.Escalate, "admin.decision.escalate", "E", "Escalate");
        RenderDecisionBar();
        return new ScrollViewer { Content = bar, HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled };
    }

    private void RenderDecisionBar()
    {
        var enabled = _session.CanDecide && !_queue.IsOffline;
        foreach (var b in _decisionButtons.Values) b.IsEnabled = enabled;
    }

    private void RenderMedia()
    {
        _media.Children.Clear();
        var detail = _session.Detail;
        if (detail == null || _player == null) return;
        var item = detail.Item;
        var shown = _c.Reveal.IsShown(item.Id, item.IsSensitive);
        if (shown)
        {
            _media.Children.Add(_player.Root);
        }
        else
        {
            _player.Pause();
            var cover = new StackPanel { Spacing = 12, Padding = new Thickness(24), MinHeight = 240, Background = new SolidColorBrush(Color(0x22, 0x22, 0x22)), HorizontalAlignment = HorizontalAlignment.Stretch };
            var text = new TextBlock { Text = L["admin.media.blurred"], TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
            var show = new Button { Content = L["admin.media.show"] + "  (S)", HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(show, L["admin.media.show"]);
            AutomationProperties.SetAcceleratorKey(show, "S");
            AutomationProperties.SetAutomationId(show, "ShowMedia");
            show.Click += (_, _) => _ = ShowAsync();
            cover.Children.Add(text);
            cover.Children.Add(show);
            AutomationProperties.SetAutomationId(cover, "BlurredMedia");
            _media.Children.Add(cover);
        }

        // Frames follow the same rule: re-decoded tiny (a blur) until shown.
        _ = RenderFramesAsync(detail);
    }

    private static Windows.UI.Color Color(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

    private int _framesGeneration;

    private async Task RenderFramesAsync(ReviewDetail detail)
    {
        var generation = ++_framesGeneration;
        _frames.Children.Clear();
        var shown = _c.Reveal.IsShown(detail.Item.Id, detail.Item.IsSensitive);
        foreach (var frame in detail.Frames.Take(24))
        {
            byte[] bytes;
            try
            {
                bytes = await _c.Service.MediaAsync(detail.Item.Id, frame.Key);
            }
            catch (ReviewException)
            {
                continue;
            }

            if (generation != _framesGeneration || _session.Detail?.Item.Id != detail.Item.Id) return;
            var image = new Image { Width = 160, Height = 90, Stretch = Stretch.UniformToFill, CanDrag = false, IsHitTestVisible = true };
            var bitmap = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, DecodePixelWidth = shown ? 320 : 8 };
            using (var ras = new InMemoryRandomAccessStream())
            {
                await ras.WriteAsync(bytes.AsBuffer());
                ras.Seek(0);
                await bitmap.SetSourceAsync(ras);
            }

            Array.Clear(bytes);
            image.Source = bitmap;
            var at = StreamPlayer.Clock(TimeSpan.FromMilliseconds(frame.AtMs));
            var name = frame.Flagged ? L.Format("admin.media.flagged", at, ReasonLabel(frame.Category ?? "other")) : L.Format("admin.media.frame", at);
            var holder = new Border { Child = image, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(frame.Flagged ? 2 : 0), BorderBrush = new SolidColorBrush(Colors.IndianRed) };
            AutomationProperties.SetName(holder, name);
            ToolTipService.SetToolTip(holder, name);
            _frames.Children.Add(holder);
        }
    }

    // ---------------- actions ----------------

    public async Task OpenAsync(string id)
    {
        _player?.Stop();
        await _session.OpenAsync(id);
    }

    public async Task<bool> ShowAsync()
    {
        var ok = await _session.RevealAsync();
        if (!ok) ShowToast(L["admin.media.showfailed"], InfoBarSeverity.Warning, undo: false);
        return ok;
    }

    public async Task<DecisionOutcome?> DecideAsync(ReviewDecision decision, string? reason = null, string? note = null)
    {
        if (!_session.CanDecide || _queue.IsOffline)
        {
            if (_queue.IsOffline) ShowToast(L["admin.result.offline"], InfoBarSeverity.Warning, undo: false);
            return null;
        }

        if (reason == null && decision is ReviewDecision.Reject or ReviewDecision.RequestChanges or ReviewDecision.Escalate)
        {
            var titleKey = decision switch
            {
                ReviewDecision.Reject => "admin.reject.title",
                ReviewDecision.RequestChanges => "admin.changes.title",
                _ => "admin.escalate.title",
            };
            var picked = await Dialogs.ReasonAsync(_root.XamlRoot, Direction, L, L[titleKey], decision != ReviewDecision.Escalate, ConfirmLabel(decision));
            if (picked == null) return null;
            reason = picked.Value.Code;
            note = picked.Value.Note;
        }

        var outcome = await _session.DecideAsync(decision, reason, note);
        Report(outcome, decision);
        if (outcome.Kind == DecisionResultKind.Done) await OpenNextAsync();
        return outcome;
    }

    private string ConfirmLabel(ReviewDecision d) => d switch
    {
        ReviewDecision.Reject => L["admin.decision.reject"],
        ReviewDecision.RequestChanges => L["admin.decision.request_changes"],
        ReviewDecision.Escalate => L["admin.decision.escalate"],
        _ => L["admin.decision.approve"],
    };

    private async Task OpenNextAsync()
    {
        var next = _queue.Tab == ReviewTab.ActionNeeded ? _queue.Items.FirstOrDefault(i => i.IsOpen && (i.AssignedTo == null || i.AssignedTo == _profile?.UserId)) : null;
        if (next != null) await OpenAsync(next.Id);
        else
        {
            _session.Close();
            RenderDetail();
        }
    }

    public async Task UndoAsync()
    {
        var outcome = await _session.UndoAsync();
        Report(outcome, ReviewDecision.Undo);
        if (outcome.Kind == DecisionResultKind.Undone && outcome.Item is { } item && _session.Detail?.Item.Id != item.Id) await OpenAsync(item.Id);
    }

    private void Report(DecisionOutcome outcome, ReviewDecision decision)
    {
        switch (outcome.Kind)
        {
            case DecisionResultKind.Done:
                ShowToast(L.Format("admin.toast.done", DecidedWord(Wire.Of(decision))), InfoBarSeverity.Success, undo: true);
                break;
            case DecisionResultKind.Undone:
                ShowToast(L["admin.toast.undone"], InfoBarSeverity.Informational, undo: false);
                break;
            case DecisionResultKind.Conflict:
                var c = outcome.Conflict!;
                var key = c.Decision is { } d && L["admin.conflict." + d] is var s && !s.StartsWith("admin.", StringComparison.Ordinal) ? "admin.conflict." + d : "admin.conflict.unknown";
                ShowToast(L.Format(key, c.ByName ?? L["admin.conflict.someone"]), InfoBarSeverity.Warning, undo: false);
                break;
            case DecisionResultKind.ClaimedByOther:
                ShowToast(L.Format("admin.result.claimed", (outcome.Error as ClaimedByOtherException)?.Name ?? L["admin.conflict.someone"]), InfoBarSeverity.Warning, undo: false);
                break;
            case DecisionResultKind.Offline:
                ShowToast(L["admin.result.offline"], InfoBarSeverity.Warning, undo: false);
                break;
            case DecisionResultKind.ReasonRequired:
                ShowToast(L["admin.reason.required"], InfoBarSeverity.Warning, undo: false);
                break;
            case DecisionResultKind.UndoExpired:
                ShowToast(L["admin.result.undoexpired"], InfoBarSeverity.Warning, undo: false);
                break;
            case DecisionResultKind.StepUpCancelled:
                ShowToast(L["admin.result.stepup"], InfoBarSeverity.Warning, undo: false);
                break;
            default:
                ShowToast(outcome.Error is SeniorOnlyException ? L["admin.result.senior"] : L["admin.error.generic"], InfoBarSeverity.Error, undo: false);
                break;
        }
    }

    private string ErrorText(ReviewException ex) => ex switch
    {
        OfflineException => L["admin.result.offline"],
        SeniorOnlyException => L["admin.result.senior"],
        _ => L["admin.error.generic"],
    };

    public void ShowToast(string message, InfoBarSeverity severity, bool undo)
    {
        LastToast = message;
        _toast.Message = message;
        _toast.Severity = severity;
        if (undo)
        {
            var b = new Button { Content = L["admin.undo"] + "  (U)" };
            AutomationProperties.SetName(b, L["admin.undo"]);
            AutomationProperties.SetAcceleratorKey(b, "U");
            AutomationProperties.SetAutomationId(b, "Undo");
            b.Click += (_, _) => _ = UndoAsync();
            _toast.ActionButton = b;
        }
        else
        {
            _toast.ActionButton = null;
        }

        _toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Interval = undo ? ReviewSession.UndoWindow : TimeSpan.FromSeconds(8);
        _toastTimer.Start();
        if (FrameworkElementAutomationPeer.FromElement(_toast) is { } peer) peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, message, "admin-toast");
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _c.Reveal.Input();
        if (_state != AdminState.Ready || _hidden) return;
        var focused = FocusManager.GetFocusedElement(_root.XamlRoot);
        if (focused is TextBox or AutoSuggestBox or ComboBox or PasswordBox || (focused is FrameworkElement fe && fe.Parent is AutoSuggestBox)) return;
        if (e.Key is VirtualKey.Up or VirtualKey.Down && focused is ListViewItem) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var name = e.Key switch
        {
            VirtualKey.Space => "Space",
            (VirtualKey)191 => "Number191",
            _ => e.Key.ToString(),
        };
        var command = ReviewShortcuts.For(name, ctrl, shift);
        if (command == ReviewCommand.None) return;
        e.Handled = true;
        _ = RunAsync(command);
    }

    public async Task RunAsync(ReviewCommand command)
    {
        switch (command)
        {
            case ReviewCommand.Next:
            case ReviewCommand.Previous:
                var items = _queue.Items;
                if (items.Count == 0) return;
                var index = items.ToList().FindIndex(i => i.Id == _session.Detail?.Item.Id);
                index = command == ReviewCommand.Next ? Math.Min(items.Count - 1, index + 1) : Math.Max(0, index - 1);
                await OpenAsync(items[index].Id);
                break;
            case ReviewCommand.Open:
                if (_list.SelectedItem is FrameworkElement { Tag: string id }) await OpenAsync(id);
                break;
            case ReviewCommand.Approve: await DecideAsync(ReviewDecision.Approve); break;
            case ReviewCommand.Reject: await DecideAsync(ReviewDecision.Reject); break;
            case ReviewCommand.Escalate: await DecideAsync(ReviewDecision.Escalate); break;
            case ReviewCommand.RequestChanges: await DecideAsync(ReviewDecision.RequestChanges); break;
            case ReviewCommand.Undo: await UndoAsync(); break;
            case ReviewCommand.Show: await ShowAsync(); break;
            case ReviewCommand.PlayPause: _player?.Toggle(); break;
            case ReviewCommand.Search: _search.Focus(FocusState.Keyboard); break;
            case ReviewCommand.Refresh: await _queue.RefreshAsync(); break;
            case ReviewCommand.Back:
                _session.Close();
                RenderDetail();
                _list.Focus(FocusState.Keyboard);
                break;
            case ReviewCommand.Help:
                await Dialogs.ShortcutsAsync(_root.XamlRoot, Direction, L);
                break;
        }
    }

    public void ShowPushState(string? message)
    {
        _c.PushMessage = message;
        _push.Message = message ?? "";
        _push.IsOpen = !string.IsNullOrEmpty(message);
    }

    /// <summary>Toast "Open" / "Claim": bring the item up (claiming happens on open).</summary>
    public async Task ActivateReviewAsync(string reviewId)
    {
        if (_state != AdminState.Ready) return;
        _queue.Tab = ReviewTab.ActionNeeded;
        await OpenAsync(reviewId);
    }
}
