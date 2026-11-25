using AlUmmahNow.App.Theme;
using AlUmmahNow.Contrast;
using System.Runtime.InteropServices;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Passkeys;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Updates.Views;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// The Studio window (Lantern Glass): Mica window; a night title bar with the lantern string, the app
/// name, video search and the caption buttons; an Acrylic sidebar headed by the one identity menu
/// (channels and people) with the pages under it, an icon rail in narrow windows; the page with its
/// title. Sign-in, loading and error states replace the sidebar and page.
/// </summary>
internal sealed class StudioWindow
{
    private static readonly Logger Logger = Log.For("ui");
    private const double SidebarWidth = 264;
    private const double RailWidth = 72;

    private readonly Localizer _l;
    private readonly StudioShellViewModel _shell;
    private readonly FlowDirection _direction;
    private readonly Grid _body = new();
    private readonly Grid _studio = new();
    private readonly Border _sidebar;
    private readonly ListView _nav = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false };
    // Settings sits at the foot of the sidebar.
    private readonly ListView _navFooter = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false };
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _pageTitle = new() { FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Dictionary<string, (FrameworkElement Page, string Title, ListViewItem Item, TextBlock Label)> _pages = new();
    private readonly Button _identityButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Flyout _identityFlyout = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private readonly Border _search;
    private TextBox? _searchText;
    private bool _readySettled;
    private readonly Grid _titleBar = new() { Height = 48 };
    private readonly List<ListViewItem> _order = new();
    private StudioPages? _studioPages;
    private bool _syncingPages;
    private bool _rail;
    private readonly StackPanel _notices = new();
    private readonly Grid _root;
    private UpdateViews? _updates;
    // The web Fawanees header's colours for the app's theme (Light or Dark), from the registry's web tokens.
    private readonly FawaneesHeader _header = FawaneesHeader.For(Application.Current.RequestedTheme == ApplicationTheme.Dark);

    public StudioWindow(Localizer l, StudioShellViewModel shell, string? banner)
    {
        _l = l;
        _shell = shell;
        _direction = l.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        Window = new Window { Title = l["studio.name"], SystemBackdrop = new MicaBackdrop() };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);
        // Arabic: a right-to-left window, so Windows puts the caption buttons on the left.
        if (l.IsRtl) SetWindowLongPtr(hwnd, GwlExStyle, GetWindowLongPtr(hwnd, GwlExStyle) | WsExLayoutRtl);

        _search = SearchPill(l["studio.videos.search"], text =>
        {
            ShowPage(StudioPages.Videos);
            SearchRequested?.Invoke(this, text);
        });

        BuildTitleBar();
        _identityButton.Flyout = _identityFlyout;
        _identityButton.Style = IdentityButtonStyle();
        AutomationProperties.SetAutomationId(_identityButton, "IdentityMenu");
        _identityFlyout.FlyoutPresenterStyle = MenuStyle();

        foreach (var list in new[] { _nav, _navFooter })
        {
            var other = list == _nav ? _navFooter : _nav;
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is not ListViewItem { Tag: string name } || !_pages.TryGetValue(name, out var p)) return;
                other.SelectedItem = null;
                _page.Content = new ScrollViewer { Content = p.Page, HorizontalScrollMode = ScrollMode.Disabled, Padding = new Thickness(12, 0, 12, 24) };
                _pageTitle.Text = p.Title;
                if (!_syncingPages) _studioPages?.Select(name);
            };
        }

        var side = new Grid { Padding = new Thickness(10, 12, 10, 12) };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_navFooter, 2);
        side.Children.Add(_navFooter);
        side.Children.Add(LanternGlass.WithGlow(_identityButton, 12, 0.28f, 22f));
        Grid.SetRow(_nav, 1);
        _nav.Margin = new Thickness(0, 14, 0, 0);
        side.Children.Add(_nav);
        _sidebar = new Border
        {
            Background = LanternGlass.Res("StudioSidebarBrush"),
            BorderBrush = LanternGlass.Res("StudioEdgeBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = side,
            Width = SidebarWidth,
        };

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new Border { Padding = new Thickness(28, 20, 28, 10), Child = _pageTitle };
        AutomationProperties.SetHeadingLevel(_pageTitle, AutomationHeadingLevel.Level1);
        main.Children.Add(heading);
        Grid.SetRow(_page, 1);
        main.Children.Add(_page);

        _studio.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _studio.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _studio.Children.Add(_sidebar);
        Grid.SetColumn(main, 1);
        _studio.Children.Add(main);

        var root = _root = new Grid { FlowDirection = _direction, Background = LanternGlass.Res("StudioPageBrush") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_titleBar);
        if (!string.IsNullOrEmpty(banner))
        {
            var info = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = banner };
            _notices.Children.Add(info);
        }

        Grid.SetRow(_notices, 1);
        root.Children.Add(_notices);

        Grid.SetRow(_body, 2);
        root.Children.Add(_body);
        // Every brush follows high contrast itself: no box behind a text, which hid the selected page's highlight
        root.HighContrastAdjustment = ElementHighContrastAdjustment.None;
        Window.Content = root;

        // The window opens at rest, as the web does: WinUI focuses the first field (the search), so focus
        // moves to the page list once, which is already a tab stop (Tab order unchanged).
        root.Loaded += (_, _) => Window.DispatcherQueue.TryEnqueue(Settle);
        // Ctrl+K or "/" jump into the search; "/" only when you're not typing in a field.
        root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        var ctrlK = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = global::Windows.System.VirtualKey.K, Modifiers = global::Windows.System.VirtualKeyModifiers.Control };
        ctrlK.Invoked += (_, e) => e.Handled = FocusSearch();
        root.KeyboardAccelerators.Add(ctrlK);
        foreach (var key in new[] { (global::Windows.System.VirtualKey)191, global::Windows.System.VirtualKey.Divide })
        {
            var slash = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key };
            slash.Invoked += (_, e) => e.Handled = !Typing() && FocusSearch();
            root.KeyboardAccelerators.Add(slash);
        }

        Window.ExtendsContentIntoTitleBar = true;
        Window.SetTitleBar(_titleBar);
        var titleBar = Window.AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        PaintCaption(titleBar);
        SystemBrushes.Painted += () =>
        {
            PaintCaption(titleBar);
            RenderIdentity();
        };

        // A minimum size instead of a phone layout: nothing clips or truncates at any size it can take.
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (Window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(StudioWindowSize.MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(StudioWindowSize.MinHeight * scale);
        }

        var (width, height) = StudioWindowSize.Initial(Environment.GetEnvironmentVariable("AUN_WINDOW_SIZE"));
        Window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)));
        Window.SizeChanged += (_, e) => SetRail(StudioWindowSize.IsRail(e.Size.Width));
        SetRail(StudioWindowSize.IsRail(width));

        _shell.PropertyChanged += (_, _) => Render();
        Render();
    }

    /// <summary>The caption buttons over the header band, which is night blue in Light and Dark, so their glyphs are always light.</summary>
    private void PaintCaption(AppWindowTitleBar bar)
    {
        var high = LanternGlass.IsHighContrast;
        Color C(Rgba color, HighContrastRole role) => high ? SystemBrushes.ColorOf(role) : LanternGlass.C(color);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        // The web header's icon, hover and pressed colours (nav-icon, nav-hover, nav-active-*).
        bar.ButtonForegroundColor = C(_header.Icon, HighContrastRole.Text);
        bar.ButtonInactiveForegroundColor = C(_header.Icon, HighContrastRole.Text);
        bar.ButtonHoverBackgroundColor = C(_header.Hover, HighContrastRole.Highlight);
        bar.ButtonHoverForegroundColor = C(_header.Text, HighContrastRole.HighlightText);
        bar.ButtonPressedBackgroundColor = C(_header.ActiveBackground, HighContrastRole.Highlight);
        bar.ButtonPressedForegroundColor = C(_header.ActiveText, HighContrastRole.HighlightText);
    }

    public Window Window { get; }

    /// <summary>The update bar under the header, and the screen of an unsupported version in place of everything under it.</summary>
    public void UseUpdates(UpdateViews updates)
    {
        _updates = updates;
        _notices.Children.Insert(0, updates.Bar);
        Grid.SetRow(updates.Forced, 1);
        Grid.SetRowSpan(updates.Forced, 2);
        _root.Children.Add(updates.Forced);
        updates.ForcedChanged += ApplyForced;
        ApplyForced();
    }

    private void ApplyForced()
    {
        if (_updates is not { } updates) return;
        _identityFlyout.Hide();
        _notices.Visibility = _body.Visibility = updates.IsForced ? Visibility.Collapsed : Visibility.Visible;
        Render();
        if (updates.IsForced) updates.FocusForced();
    }

    /// <summary>CI: whether the header search has keyboard focus (it must not when the window opens).</summary>
    public bool SearchHasFocus => _searchText?.XamlRoot is { } r && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(r), _searchText);

    /// <summary>CI: whether the pointer is over the header search.</summary>
    public bool SearchIsHovered { get; private set; }

    /// <summary>Moves focus off the header search when the window (or the signed-in Studio) first appears.</summary>
    /// <summary>CI: whether any text field has keyboard focus (none may when the window opens).</summary>
    public bool FieldHasFocus => Typing();

    private void Settle()
    {
        // The header search, or a page's own field (WinUI focuses the first one it finds).
        if (!SearchHasFocus && !Typing()) return;
        if (_nav.SelectedItem is ListViewItem selected) selected.Focus(FocusState.Programmatic);
        else _nav.Focus(FocusState.Programmatic);
    }

    private bool Typing() => Window.Content?.XamlRoot is { } r && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(r) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox;

    /// <summary>The header search takes the keyboard (Control+K, "/", and a CI scene).</summary>
    public bool FocusSearch() => _search.Visibility == Visibility.Visible && _searchText != null && _searchText.Focus(FocusState.Keyboard);

    /// <summary>The title-bar search was submitted: the Videos page searches for the text.</summary>
    public event EventHandler<string>? SearchRequested;

    private const int GwlExStyle = -20;
    private const long WsExLayoutRtl = 0x00400000;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);

    private void BuildTitleBar()
    {
        _titleBar.Children.Add(LanternGlass.NightBand(_header));
        var row = new Grid { Padding = new Thickness(14, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Room for the caption buttons (138 px at 100 %), on whichever side Windows draws them.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
        var app = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        // The videos app's icon (the owner's art, as is).
        var icon = new Image { Width = 22, Height = 22 };
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 44 };
        _ = bitmap.SetSourceAsync(StudioTheme.AppIcon().AsRandomAccessStream());
        icon.Source = bitmap;
        app.Children.Add(icon);
        app.Children.Add(new TextBlock { Text = _l["studio.name"], Foreground = LanternGlass.Brush(LanternGlass.C(_header.Text), HighContrastRole.Text), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(app);
        Grid.SetColumn(_search, 1);
        row.Children.Add(_search);
        _titleBar.Children.Add(row);
        // The search box takes clicks; the rest of the band drags the window.
        _search.SizeChanged += (_, _) => UpdatePassthrough();
        _titleBar.SizeChanged += (_, _) => UpdatePassthrough();
    }

    /// <summary>
    /// The web header's search field (<c>.ah-search</c>), shape and colour: a 40 px pill with a 1 px
    /// nav-search-border (2 px upload-focus ring while typing), the text 15 px with 16 px at its start,
    /// and a 52 px search button at its end behind a 1 px divider, nav-icon with nav-hover on hover.
    /// </summary>
    private Border SearchPill(string label, Action<string> submit)
    {
        var h = _header;
        var border = LanternGlass.Brush(LanternGlass.C(h.SearchBorder), HighContrastRole.Text);
        var text = new TextBox
        {
            PlaceholderText = label,
            // Empty, it has no direction to detect: in Arabic the caret was at the far end, away from the placeholder
            TextReadingOrder = TextReadingOrder.UseFlowDirection,
            TextAlignment = TextAlignment.Start,
            // The style sets its own, over the field's resources
            PlaceholderForeground = LanternGlass.Brush(LanternGlass.C(h.SearchPlaceholder), HighContrastRole.Text),
            FontSize = FawaneesHeader.SearchFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(FawaneesHeader.SearchTextInset, 0, 8, 0),
            BorderThickness = new Thickness(0),
            MinHeight = 0,
        };
        // Lightweight styling: no box of its own; the pill draws the field.
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var state in new[] { "", "PointerOver", "Focused" })
        {
            text.Resources["TextControlBackground" + state] = clear;
            text.Resources["TextControlBorderBrush" + state] = clear;
            text.Resources["TextControlForeground" + state] = LanternGlass.Brush(LanternGlass.C(h.SearchText), HighContrastRole.Text);
            // Hovered and focused, the template takes these; at rest the property above
            text.Resources["TextControlPlaceholderForeground" + state] = LanternGlass.Brush(LanternGlass.C(h.SearchPlaceholder), HighContrastRole.Text);
        }

        text.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        AutomationProperties.SetName(text, label);
        AutomationProperties.SetAutomationId(text, "TitleSearch");
        AutomationProperties.SetAcceleratorKey(text, "Control+K");
        _searchText = text;
        text.PointerEntered += (_, _) => SearchIsHovered = true;
        text.PointerExited += (_, _) => SearchIsHovered = false;
        text.KeyDown += (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            submit(text.Text);
        };

        var go = new Button
        {
            Width = FawaneesHeader.SearchButtonWidth,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Background = clear,
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = border,
            Foreground = LanternGlass.Brush(LanternGlass.C(h.Icon), HighContrastRole.Text),
            Content = new FontIcon { Glyph = "\uE721", FontSize = 18 },
        };
        go.Resources["ButtonBackgroundPointerOver"] = LanternGlass.Brush(LanternGlass.C(h.Hover), HighContrastRole.Window);
        go.Resources["ButtonBackgroundPressed"] = LanternGlass.Brush(LanternGlass.C(h.Hover), HighContrastRole.Window);
        go.Resources["ButtonForegroundPointerOver"] = LanternGlass.Brush(LanternGlass.C(h.Icon), HighContrastRole.Text);
        go.Resources["ButtonForegroundPressed"] = LanternGlass.Brush(LanternGlass.C(h.Icon), HighContrastRole.Text);
        go.Resources["ButtonBorderBrushPointerOver"] = border;
        go.Resources["ButtonBorderBrushPressed"] = border;
        AutomationProperties.SetName(go, label);
        go.Click += (_, _) => submit(text.Text);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        Grid.SetColumn(go, 1);
        row.Children.Add(go);
        var pill = new Border
        {
            Height = FawaneesHeader.SearchHeight,
            Width = FawaneesHeader.SearchMaxWidth,
            VerticalAlignment = VerticalAlignment.Center,
            // --r-pill (999px) on a 40 px field: half the height rounds the ends fully.
            CornerRadius = new CornerRadius(Math.Min(h.SearchRadius, FawaneesHeader.SearchHeight / 2)),
            Background = LanternGlass.Brush(LanternGlass.C(h.SearchBackground), HighContrastRole.Window),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Child = row,
        };
        text.GotFocus += (_, _) =>
        {
            pill.BorderBrush = LanternGlass.Brush(LanternGlass.C(h.SearchFocus), HighContrastRole.Highlight);
            pill.BorderThickness = new Thickness(2);
        };
        text.LostFocus += (_, _) =>
        {
            pill.BorderBrush = border;
            pill.BorderThickness = new Thickness(1);
        };
        return pill;
    }

    private void UpdatePassthrough()
    {
        if (_search.XamlRoot is not { } root || _search.ActualWidth <= 0) return;
        var bounds = _search.TransformToVisual(null).TransformBounds(new global::Windows.Foundation.Rect(0, 0, _search.ActualWidth, _search.ActualHeight));
        var s = root.RasterizationScale;
        var rect = new global::Windows.Graphics.RectInt32((int)Math.Round(bounds.X * s), (int)Math.Round(bounds.Y * s), (int)Math.Round(bounds.Width * s), (int)Math.Round(bounds.Height * s));
        InputNonClientPointerSource.GetForWindowId(Window.AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
    }

    private void SetRail(bool rail)
    {
        _rail = rail;
        _sidebar.Width = rail ? RailWidth : SidebarWidth;
        foreach (var p in _pages.Values) p.Label.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;
        _search.Width = rail ? FawaneesHeader.SearchNarrowWidth : FawaneesHeader.SearchMaxWidth;
        RenderIdentity();
    }

    public void AddPage(string name, string title, Symbol icon, FrameworkElement page)
    {
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Padding = new Thickness(4, 0, 4, 0) };
        // The lines of a list begin where its text does; the other icons are the same in both directions
        row.Children.Add(icon == Symbol.List ? (IconElement)new FontIcon { Glyph = "\uE14C", FontSize = 20, MirroredWhenRightToLeft = true } : new SymbolIcon(icon));
        row.Children.Add(label);
        var item = new ListViewItem { Content = row, Tag = name, MinHeight = 42, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8) };
        AutomationProperties.SetAutomationId(item, "Page_" + name);
        AutomationProperties.SetName(item, title);
        ToolTipService.SetToolTip(item, title);
        label.Visibility = _rail ? Visibility.Collapsed : Visibility.Visible;
        _pages[name] = (page, title, item, label);
        if (name == StudioPages.Settings)
        {
            _navFooter.Items.Add(item);
        }
        else
        {
            _order.Add(item);
            _nav.Items.Add(item);
        }
    }

    private IEnumerable<ListViewItem> NavItems => _nav.Items.Concat(_navFooter.Items).OfType<ListViewItem>();

    /// <summary>Signs an account out after the in-app confirmation (Settings).</summary>
    public Task SignOut(StudioAccount account) => SignOutAsync(account);

    public void ShowPage(string name)
    {
        var items = NavItems.ToList();
        var item = items.FirstOrDefault(i => (string)i.Tag == name && i.Visibility == Visibility.Visible)
            ?? items.FirstOrDefault(i => i.Visibility == Visibility.Visible);
        if (item == null) return;
        if (_navFooter.Items.Contains(item)) _navFooter.SelectedItem = item;
        else _nav.SelectedItem = item;
    }

    /// <summary>Hidden-when-off pages: a page whose feature is off leaves the sidebar, and the window shows <see cref="StudioPages.Current"/>.</summary>
    public void UsePages(StudioPages pages)
    {
        _studioPages = pages;
        pages.Changed += (_, _) => ApplyPages();
        ApplyPages();
    }

    private void ApplyPages()
    {
        if (_studioPages is not { } pages) return;
        _syncingPages = true;
        try
        {
            // A page whose feature is off leaves the list entirely (a collapsed item would still reach UI Automation).
            var visible = _order.Where(i => i.Tag is not string name || pages.IsVisible(name)).ToList();
            if (!visible.SequenceEqual(_nav.Items.OfType<ListViewItem>()))
            {
                _nav.Items.Clear();
                foreach (var item in visible) _nav.Items.Add(item);
            }

            var selected = (_nav.SelectedItem ?? _navFooter.SelectedItem) as ListViewItem;
            if (selected?.Tag as string != pages.Current) ShowPage(pages.Current);
        }
        finally
        {
            _syncingPages = false;
        }
    }

    /// <summary>For CI screenshots: open the identity menu (formerly the account menu).</summary>
    public void OpenAccountMenu() => _identityFlyout.ShowAt(_identityButton);

    /// <summary>For CI screenshots: open the identity menu (formerly the "Acting as" picker).</summary>
    public void OpenChannelPicker() => _identityFlyout.ShowAt(_identityButton);

    /// <summary>For CI screenshots: show the sign-out confirmation for the active account.</summary>
    public void AskSignOut()
    {
        if (_shell.ActiveAccount is { } a) _ = SignOutAsync(a);
    }

    private void Render()
    {
        _body.Children.Clear();
        _body.Children.Add(_shell.State switch
        {
            ShellState.SignedOut => Status(_l["studio.shell.signInTitle"], _shell.PasskeysAvailable ? _l["passkey.intro"] : _l["studio.shell.signInBody"], SignInActions(), _shell.ErrorKey is { } k ? _l[k] : null),
            ShellState.SigningIn => Status(_l["auth.signingIn"], null, new ProgressRing { IsActive = true }),
            ShellState.Loading => Status(_l["studio.shell.loading"], null, new ProgressRing { IsActive = true }),
            ShellState.NoChannel => Status(_l["studio.shell.noChannelTitle"], _l["studio.shell.noChannelBody"], Action(_l["studio.shell.retry"], () => _ = _shell.RetryAsync())),
            ShellState.Error => Status(_l["studio.shell.errorTitle"], _shell.ErrorKey is { } e ? _l[e] : null, Action(_l["studio.shell.retry"], () => _ = _shell.RetryAsync())),
            ShellState.NeedsReauth => Status(_l["studio.shell.reauthTitle"], _l.Format("studio.shell.reauthBody", Localizer.Isolate(_shell.ActiveAccountName ?? "")), SignInActions()),
            _ => _studio,
        });
        _search.Visibility = _shell.State == ShellState.Ready && _updates?.IsForced != true ? Visibility.Visible : Visibility.Collapsed;
        if (_shell.State == ShellState.Ready && !_readySettled)
        {
            // The signed-in Studio replaces the sign-in state: settle once its controls exist.
            _readySettled = true;
            Window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Settle);
        }
        RenderIdentity();
    }

    /// <summary>
    /// A sign-in or status state (Lantern Glass): the app's lantern icon in a gold glow, the title and
    /// text, a spinner while waiting, and the action(s) in the card's footer, end-aligned.
    /// </summary>
    private static FrameworkElement Status(string title, string? body, FrameworkElement action, string? note = null)
    {
        var panel = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
        var icon = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 224 };
        _ = icon.SetSourceAsync(StudioTheme.AppIcon().AsRandomAccessStream());
        var lantern = LanternGlass.Picture(icon, new CornerRadius(56));
        lantern.Width = 112;
        lantern.Height = 112;
        lantern.BorderThickness = new Thickness(3);
        lantern.BorderBrush = LanternGlass.Brush(Color.FromArgb(0x99, LanternGlass.GoldEdge.R, LanternGlass.GoldEdge.G, LanternGlass.GoldEdge.B), HighContrastRole.Text);
        lantern.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(LanternGlass.WithGlow(lantern, 56, 0.6f, 40f));
        var heading = new TextBlock { Text = title, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        panel.Children.Add(heading);
        if (!string.IsNullOrEmpty(body)) panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = LanternGlass.Res("StudioMutedBrush") });
        if (!string.IsNullOrEmpty(note))
        {
            var n = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = LanternGlass.Res("StudioDangerBrush") };
            AutomationProperties.SetLiveSetting(n, AutomationLiveSetting.Assertive);
            panel.Children.Add(n);
        }

        // A spinner is part of the card; buttons go in its footer.
        var waiting = action is ProgressRing;
        if (waiting)
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(action);
        }

        var card = LanternGlass.Card(panel, waiting ? null : action, 18);
        card.Width = 520;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        var stage = new Grid
        {
            Background = new RadialGradientBrush
            {
                Center = new global::Windows.Foundation.Point(0.5, 0.42),
                GradientOrigin = new global::Windows.Foundation.Point(0.5, 0.42),
                RadiusX = 0.5,
                RadiusY = 0.6,
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x26, 0xF4, 0xAE, 0x3A), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x00, 0xF4, 0xAE, 0x3A), Offset = 1 },
                },
            },
        };
        // The owner's lantern string hangs over the sign-in stage (the header itself has no art, as on the web).
        var art = new Image { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top, Height = 150, Opacity = 0.9, IsHitTestVisible = false };
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        _ = bitmap.SetSourceAsync(StudioTheme.LanternString().AsRandomAccessStream());
        art.Source = bitmap;
        stage.Children.Add(art);
        stage.Children.Add(card);
        return stage;
    }

    /// <summary>
    /// "Sign in with a passkey" (the Windows security sheet, no browser) with "Sign in another way"
    /// (the browser sign-in) under it; only the browser button where Windows has no passkey API.
    /// </summary>
    private FrameworkElement SignInActions()
    {
        if (!_shell.PasskeysAvailable) return Action(_l["auth.signIn"], () => _ = _shell.SignInAsync(SignInMode.Default), primary: true, id: "SignIn");
        var passkey = Action(_l["passkey.signIn"], () => _ = SignInWithPasskeyAsync(), primary: true, id: "SignInPasskey");
        passkey.IsEnabled = !_shell.PasskeyBusy;
        passkey.HorizontalAlignment = HorizontalAlignment.Center;
        passkey.Loaded += (_, _) => passkey.Focus(FocusState.Programmatic);
        var other = new HyperlinkButton { Content = _l["passkey.otherWay"], HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = !_shell.PasskeyBusy };
        AutomationProperties.SetAutomationId(other, "SignIn");
        AutomationProperties.SetName(other, _l["passkey.otherWay"]);
        other.Click += (_, _) => _ = _shell.SignInAsync(SignInMode.Default);
        // In the card's footer: the other way first, the passkey (primary) at the end.
        passkey.HorizontalAlignment = HorizontalAlignment.Stretch;
        other.HorizontalAlignment = HorizontalAlignment.Stretch;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(other);
        panel.Children.Add(passkey);
        return panel;
    }

    private async Task SignInWithPasskeyAsync()
    {
        var outcome = await _shell.SignInWithPasskeyAsync();
        await ShowPasskeyOutcomeAsync(outcome);
    }

    /// <summary>"Add a passkey" from the identity menu; asks in-app before a step-up browser sign-in.</summary>
    public async Task AddPasskeyAsync()
    {
        var outcome = await _shell.AddPasskeyAsync(_ => OnUiAsync(() => Window.Content?.XamlRoot is { } root
            ? Confirm.AskAsync(root, _direction, _l["passkey.stepUp.title"], _l["passkey.stepUp.body"], _l["passkey.stepUp.continue"], _l["common.cancel"], destructive: false)
            : Task.FromResult(false)));
        if (Window.Content?.XamlRoot is not { } xamlRoot) return;
        if (outcome == PasskeyOutcome.Done) await Confirm.TellAsync(xamlRoot, _direction, _l["passkey.add"], _l["passkey.added"], _l["passkey.ok"]);
        else await ShowPasskeyOutcomeAsync(outcome);
    }

    /// <summary>One in-app dialog; closing the Windows sheet shows nothing.</summary>
    private async Task ShowPasskeyOutcomeAsync(PasskeyOutcome outcome)
    {
        if (outcome.MessageKey is not { } key || Window.Content?.XamlRoot is not { } root) return;
        if (!outcome.OfferOtherWay)
        {
            await Confirm.TellAsync(root, _direction, _l["passkey.error.title"], _l.Format(key, PasskeyOutcome.MaxPasskeys), _l["passkey.ok"]);
            return;
        }

        if (await Confirm.AskAsync(root, _direction, _l["passkey.error.title"], _l.Format(key, PasskeyOutcome.MaxPasskeys), _l["passkey.otherWay"], _l["passkey.ok"], destructive: false)) await _shell.SignInAsync(SignInMode.Default);
    }

    private Task<bool> OnUiAsync(Func<Task<bool>> show)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                tcs.TrySetResult(await show());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!queued) tcs.TrySetResult(false);
        return tcs.Task;
    }

    private static Button Action(string text, Action click, bool primary = false, string? id = null)
    {
        var b = new Button { Content = text };
        if (primary) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (id != null) AutomationProperties.SetAutomationId(b, id);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The sidebar button (the acting identity) and the one identity menu it opens.</summary>
    private void RenderIdentity()
    {
        string T(string k) => _l[k];
        var channels = IdentityMenu.Channels(_shell.Channels, _shell.ActiveChannel, T, _l.Separator);
        var people = IdentityMenu.Accounts(_shell.AccountList, _shell.ActiveAccount, a => _shell.NameOf(a), T, _l.Separator);
        var current = IdentityMenu.Current(channels, people);
        _identityButton.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        if (current == null) return;

        AutomationProperties.SetName(_identityButton, _l.Format(ActingAs.ButtonNameKey, current.Name));
        ToolTipService.SetToolTip(_identityButton, current.AccessibleName);
        var face = LanternGlass.Avatar(current.Initials, current.AvatarUrl, 36, current.Kind == IdentityKind.Account);
        if (_rail)
        {
            _identityButton.Padding = new Thickness(6);
            _identityButton.Content = face;
        }
        else
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(face);
            var text = NameAndTag(current, 14);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var chevron = new FontIcon { Glyph = "", FontSize = 12, Foreground = LanternGlass.Res("StudioMutedBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chevron, 2);
            row.Children.Add(chevron);
            _identityButton.Padding = new Thickness(10);
            _identityButton.Content = row;
        }

        var menu = new StackPanel { Spacing = 2, MinWidth = 300, MaxWidth = 360 };
        if (channels.Count > 0)
        {
            menu.Children.Add(MenuHeading(_l[IdentityMenu.ChannelsHeadingKey]));
            foreach (var r in channels)
            {
                var channel = _shell.Channels.First(c => c.Id == r.Id);
                menu.Children.Add(MenuRow(r, () => _shell.SelectChannel(channel)));
            }
        }

        if (people.Count > 0)
        {
            menu.Children.Add(Separator());
            menu.Children.Add(MenuHeading(_l[IdentityMenu.AccountsHeadingKey]));
            foreach (var r in people)
            {
                var row = MenuRow(r, () => _ = _shell.SwitchAccountAsync(r.Id));
                AutomationProperties.SetName(row, _l.Join(_l.Format("studio.shell.switchTo", r.Name), r.Tag));
                menu.Children.Add(row);
            }
        }

        menu.Children.Add(Separator());
        menu.Children.Add(MenuAction(_l["studio.shell.addAccount"], "", null, () => _ = _shell.SignInAsync(SignInMode.AddAccount)));
        var active = _shell.ActiveAccount;
        if (_shell.PasskeysAvailable && active != null && !active.NeedsReauth)
        {
            var passkey = MenuAction(_l["passkey.add"], "", "AddPasskey", () => _ = AddPasskeyAsync());
            passkey.IsEnabled = !_shell.PasskeyBusy;
            menu.Children.Add(passkey);
        }

        if (active != null)
        {
            var signOut = MenuAction(_l.Format("studio.shell.signOutOf", _shell.NameOf(active)), "", "SignOut", () => _ = SignOutAsync(active));
            signOut.Foreground = LanternGlass.Res("StudioDangerBrush");
            menu.Children.Add(signOut);
        }

        _identityFlyout.Content = menu;
    }

    private static StackPanel NameAndTag(IdentityRow r, double size)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = r.Tag, FontSize = 12, Foreground = LanternGlass.Res("StudioMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        return text;
    }

    private Button MenuRow(IdentityRow r, Action pick)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(LanternGlass.Avatar(r.Initials, r.AvatarUrl, 32, r.Kind == IdentityKind.Account));
        var text = NameAndTag(r, 13.5);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (r.IsActive)
        {
            var check = new FontIcon { Glyph = "", FontSize = 14, Foreground = LanternGlass.Brush(LanternGlass.Gold, HighContrastRole.Text), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(check, 2);
            row.Children.Add(check);
        }

        var b = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(r.IsActive ? 1 : 0),
            BorderBrush = LanternGlass.Res("StudioEdgeBrush"),
            Background = r.IsActive ? LanternGlass.Res("StudioSelectedBrush") : new SolidColorBrush(Colors.Transparent),
        };
        AutomationProperties.SetName(b, r.AccessibleName);
        AutomationProperties.SetAutomationId(b, "Identity_" + r.Id);
        b.Click += (_, _) =>
        {
            _identityFlyout.Hide();
            pick();
        };
        return b;
    }

    private Button MenuAction(string text, string glyph, string? id, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        // Only the arrow out of the door has a direction
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, Width = 32, MirroredWhenRightToLeft = glyph == "\uF3B1" });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 8, 8, 8),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        AutomationProperties.SetName(b, text);
        if (id != null) AutomationProperties.SetAutomationId(b, id);
        b.Click += (_, _) =>
        {
            _identityFlyout.Hide();
            click();
        };
        return b;
    }

    private static TextBlock MenuHeading(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = LanternGlass.Res("StudioMutedBrush"),
        Margin = new Thickness(10, 6, 10, 2),
    };

    private static Border Separator() => new() { Height = 1, Margin = new Thickness(6, 6, 6, 6), Background = LanternGlass.Res("StudioEdgeBrush") };

    private static Style IdentityButtonStyle()
    {
        var style = new Style(typeof(Button)) { BasedOn = (Style)Application.Current.Resources["DefaultButtonStyle"] };
        style.Setters.Add(new Setter(Control.BackgroundProperty, LanternGlass.Res("StudioCardBrush")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, LanternGlass.Res("StudioEdgeBrush")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));
        return style;
    }

    private static Style MenuStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, LanternGlass.Res("StudioMenuBrush")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, LanternGlass.Res("StudioEdgeStrongBrush")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(14)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 400.0));
        return style;
    }

    private async Task SignOutAsync(StudioAccount account)
    {
        if (Window.Content?.XamlRoot is not { } root) return;
        var ok = await Confirm.AskAsync(root, _direction, _l.Format("studio.shell.signOutTitle", Localizer.Isolate(_shell.NameOf(account))), _l["studio.shell.signOutBody"], _l["auth.signOut"], _l["common.cancel"]);
        if (!ok) return;
        try
        {
            await _shell.SignOutAsync(account.UserId);
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
        {
            Logger.Error("sign-out failed", ex);
        }
    }
}
