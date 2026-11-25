using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Upload;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// The field of the date and time the first video publishes at, and the month of days and the time that open from it
/// (<see cref="SchedulePick"/>). The days stay the same controls while the month changes: focus and Narrator stay put.
/// </summary>
internal sealed class SchedulePicker
{
    private readonly Localizer _l;
    private readonly SchedulePick _pick;
    private readonly Button _field = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _value = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _error;
    private readonly TextBlock _errorInside;
    private readonly TextBlock _month = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly Button _back;
    private readonly Button _next;
    private readonly ToggleButton[] _days = new ToggleButton[42];
    private readonly DateTime[] _dates = new DateTime[42];
    private readonly ComboBox _hour;
    private readonly ComboBox _minute;
    private readonly ComboBox _half;
    private readonly Flyout _flyout;
    private bool _rendering;

    public SchedulePicker(Localizer l, SchedulePick pick, Action picked)
    {
        _l = l;
        _pick = pick;
        var direction = l.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _error = Error();
        _errorInside = Error();

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = "", FontSize = 16 };
        Grid.SetColumn(icon, 1);
        row.Children.Add(_value);
        row.Children.Add(icon);
        _field.Content = row;
        AutomationProperties.SetAutomationId(_field, "ScheduleStart");

        _back = Turn("", "studio.upload.scheduleMonthBack", pick.Back);
        _next = Turn("", "studio.upload.scheduleMonthNext", pick.Next);
        var head = new Grid { ColumnSpacing = 4 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_back, 1);
        Grid.SetColumn(_next, 2);
        AutomationProperties.SetHeadingLevel(_month, AutomationHeadingLevel.Level3);
        AutomationProperties.SetLiveSetting(_month, AutomationLiveSetting.Polite);
        head.Children.Add(_month);
        head.Children.Add(_back);
        head.Children.Add(_next);

        // The arrow keys move between the days, and Tab past them
        var days = new Grid { ColumnSpacing = 2, RowSpacing = 2, XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled, TabFocusNavigation = KeyboardNavigationMode.Once };
        for (var i = 0; i < 7; i++) days.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        for (var i = 0; i < 7; i++) days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(i == 0 ? 24 : 36) });
        var weekdays = pick.Weekdays;
        for (var i = 0; i < 7; i++)
        {
            var name = new TextBlock { Text = weekdays[i].Label, FontSize = 12, Foreground = LanternGlass.Res("StudioMutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(name, weekdays[i].Name);
            Grid.SetColumn(name, i);
            days.Children.Add(name);
        }

        for (var i = 0; i < _days.Length; i++)
        {
            var at = i;
            var day = _days[i] = new ToggleButton { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0) };
            day.Click += (_, _) =>
            {
                pick.Pick(_dates[at]);
                // A click on the day that is picked leaves it picked
                Render();
            };
            Grid.SetColumn(day, i % 7);
            Grid.SetRow(day, 1 + (i / 7));
            days.Children.Add(day);
        }

        _hour = Box("studio.upload.scheduleHour", pick.Hours, i => pick.Hour = i);
        _minute = Box("studio.upload.scheduleMinute", pick.MinuteLabels, i => pick.Minute = pick.Minutes[i]);
        _half = Box("studio.upload.scheduleHalf", pick.Halves, i => pick.IsAfternoon = i == 1);
        // A time reads left to right in Arabic too, with its half of the day after it
        var clock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, FlowDirection = FlowDirection.LeftToRight };
        clock.Children.Add(_hour);
        clock.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center });
        clock.Children.Add(_minute);
        var time = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        time.Children.Add(clock);
        time.Children.Add(_half);
        _flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom, FlyoutPresenterStyle = StudioWindow.MenuStyle() };
        var done = new Button { Content = l["studio.upload.scheduleDone"], Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAutomationId(done, "ScheduleDone");
        done.Click += (_, _) => _flyout.Hide();
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(6), FlowDirection = direction, HighContrastAdjustment = ElementHighContrastAdjustment.None };
        panel.Children.Add(head);
        panel.Children.Add(days);
        panel.Children.Add(time);
        panel.Children.Add(_errorInside);
        panel.Children.Add(done);
        _flyout.Content = panel;
        // The day that is picked has the focus
        _flyout.Opened += (_, _) => (_days.FirstOrDefault(d => d.IsChecked == true && d.IsEnabled) ?? _days.FirstOrDefault(d => d.IsEnabled))?.Focus(FocusState.Programmatic);
        // Time has passed since it was drawn
        _flyout.Opening += (_, _) =>
        {
            pick.Open();
            Render();
        };
        _field.Flyout = _flyout;

        Root.Children.Add(new TextBlock { Text = l["studio.upload.scheduleStart"], TextWrapping = TextWrapping.Wrap });
        Root.Children.Add(_field);
        Root.Children.Add(_error);
        pick.Changed += (_, _) =>
        {
            picked();
            Render();
        };
        Render();
    }

    public StackPanel Root { get; } = new() { Spacing = 6 };

    /// <summary>CI screenshots: the month, open.</summary>
    public void Open() => _flyout.ShowAt(_field);

    /// <summary>CI's scenarios: the days that can be picked, and the one that is, can be read where they are drawn.</summary>
    public bool IsReadable => _flyout.IsOpen && _days.Count(d => d.IsChecked == true) == 1
        && _days.Where(d => d.IsChecked == true).All(d => Presenter(d) is { } p && LanternGlass.IsReadable(p.Foreground, p.Background));

    /// <summary>The time passes too: a field that was right says when it no longer is.</summary>
    public void Render()
    {
        _rendering = true;
        var label = _pick.Label;
        _value.Text = label;
        AutomationProperties.SetName(_field, _l.Join(_l["studio.upload.scheduleStart"], label));
        _error.Visibility = _errorInside.Visibility = _pick.IsTooSoon ? Visibility.Visible : Visibility.Collapsed;
        _month.Text = _pick.MonthLabel;
        _back.IsEnabled = _pick.CanGoBack;
        _next.IsEnabled = _pick.CanGoNext;
        var days = _pick.Days;
        for (var i = 0; i < _days.Length; i++)
        {
            var at = i - _pick.LeadingBlanks;
            var day = at >= 0 && at < days.Count ? days[at] : null;
            var button = _days[i];
            button.Visibility = day == null ? Visibility.Collapsed : Visibility.Visible;
            button.IsChecked = day is { IsSelected: true };
            button.IsEnabled = day is { IsEnabled: true };
            if (day == null) continue;
            _dates[i] = day.Date;
            button.Content = day.Label;
            AutomationProperties.SetName(button, day.Name);
        }

        _hour.SelectedIndex = _pick.Hour;
        _minute.SelectedIndex = _pick.Minutes.ToList().IndexOf(_pick.Minute);
        _half.SelectedIndex = _pick.IsAfternoon ? 1 : 0;
        _rendering = false;
    }

    private TextBlock Error()
    {
        var error = new TextBlock { Text = _l.Format("studio.upload.scheduleTooSoon", (int)SchedulePick.Lead.TotalMinutes), TextWrapping = TextWrapping.Wrap, MaxWidth = 292, HorizontalAlignment = HorizontalAlignment.Left, Foreground = LanternGlass.Res("StudioDangerBrush") };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        return error;
    }

    private Button Turn(string glyph, string key, Action turn)
    {
        // The arrows follow the language's direction
        var button = new Button { Width = 36, Height = 32, Padding = new Thickness(0), Content = new FontIcon { Glyph = glyph, FontSize = 12, MirroredWhenRightToLeft = true } };
        AutomationProperties.SetName(button, _l[key]);
        ToolTipService.SetToolTip(button, _l[key]);
        button.Click += (_, _) => turn();
        return button;
    }

    private ComboBox Box(string key, IReadOnlyList<string> items, Action<int> set)
    {
        var box = new ComboBox { ItemsSource = items, MinWidth = 84 };
        AutomationProperties.SetName(box, _l[key]);
        box.SelectionChanged += (_, _) =>
        {
            if (_rendering || box.SelectedIndex < 0) return;
            // Not inside the change itself: an hour the clocks skip changes the list back
            var at = box.SelectedIndex;
            box.DispatcherQueue.TryEnqueue(() => set(at));
        };
        return box;
    }

    private static ContentPresenter? Presenter(DependencyObject part)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(part); i++)
        {
            var child = VisualTreeHelper.GetChild(part, i);
            if (child is ContentPresenter found) return found;
            if (Presenter(child) is { } deeper) return deeper;
        }

        return null;
    }
}
