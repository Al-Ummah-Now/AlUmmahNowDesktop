using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Upload;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// The field of the date and time the first video publishes at, and the month of days and the time that open from it
/// (<see cref="SchedulePick"/>). The days stay the same buttons while the month changes: the focus stays put.
/// </summary>
internal sealed class SchedulePicker
{
    private readonly Localizer _l;
    private readonly SchedulePick _pick;
    private readonly Gtk.Label _value = Ui.Label("");
    private readonly Gtk.Label _error;
    private readonly Gtk.Label _errorInside;
    private readonly Gtk.Label _month = Ui.Label("");
    private readonly Gtk.Button _back;
    private readonly Gtk.Button _next;
    private readonly Gtk.ToggleButton[] _days = new Gtk.ToggleButton[42];
    private readonly Gtk.Label[] _labels = new Gtk.Label[42];
    private readonly DateTime[] _dates = new DateTime[42];
    private readonly List<Gtk.Label> _names = new();
    private readonly Gtk.DropDown _hour;
    private readonly Gtk.DropDown _minute;
    private readonly Gtk.DropDown _half;
    private readonly Gtk.Popover _popover = Gtk.Popover.New();
    private bool _rendering;

    public SchedulePicker(Localizer l, SchedulePick pick, Action picked)
    {
        _l = l;
        _pick = pick;
        _error = Error();
        _errorInside = Error();
        Field.AddCssClass("schedule-field");
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _value.SetHexpand(true);
        row.Append(_value);
        row.Append(Gtk.Image.NewFromIconName("x-office-calendar-symbolic"));
        Field.SetChild(row);

        _back = Turn("go-previous-symbolic", "studio.upload.scheduleMonthBack", pick.Back);
        _next = Turn("go-next-symbolic", "studio.upload.scheduleMonthNext", pick.Next);
        _month.AddCssClass("heading");
        _month.SetHexpand(true);
        _month.SetWrap(false);
        var head = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        head.Append(_month);
        head.Append(_back);
        head.Append(_next);

        var days = Gtk.Grid.New();
        days.SetColumnHomogeneous(true);
        days.SetColumnSpacing(2);
        days.SetRowSpacing(2);
        var weekdays = pick.Weekdays;
        for (var i = 0; i < 7; i++)
        {
            var name = Ui.Label(weekdays[i].Label, dim: true, start: false);
            name.AddCssClass("caption");
            name.SetXalign(0.5f);
            name.SetWrap(false);
            Ui.Say(name, weekdays[i].Name);
            days.Attach(name, i, 0, 1, 1);
        }

        // Six weeks have their place in every month: the arrows above stay where they are
        for (var i = 1; i < 7; i++)
        {
            var week = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            week.SetSizeRequest(-1, 32);
            days.Attach(week, 0, i, 1, 1);
        }

        for (var i = 0; i < _days.Length; i++)
        {
            var at = i;
            var day = _days[i] = Gtk.ToggleButton.New();
            // Not the button's own label, whose text is then its name ("22")
            day.SetChild(_labels[i] = Gtk.Label.New(""));
            day.AddCssClass("flat");
            day.AddCssClass("schedule-day");
            day.OnToggled += (_, _) =>
            {
                if (_rendering) return;
                pick.Pick(_dates[at]);
                // A click on the day that is picked leaves it picked
                Render();
            };
            days.Attach(day, i % 7, 1 + (i / 7), 1, 1);
        }

        _hour = Box("studio.upload.scheduleHour", pick.Hours, i => pick.Hour = i);
        _minute = Box("studio.upload.scheduleMinute", pick.MinuteLabels, i => pick.Minute = pick.Minutes[i]);
        _half = Box("studio.upload.scheduleHalf", pick.Halves, i => pick.IsAfternoon = i == 1);
        // A time reads left to right in Arabic too, with its half of the day after it
        var clock = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        clock.SetDirection(Gtk.TextDirection.Ltr);
        clock.Append(_hour);
        clock.Append(Gtk.Label.New(":"));
        clock.Append(_minute);
        var time = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        time.Append(clock);
        time.Append(_half);
        foreach (var name in _names) time.Append(name);
        var done = Ui.Button(l["studio.upload.scheduleDone"], _popover.Popdown, "suggested-action");
        done.SetHalign(Gtk.Align.End);

        var panel = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        panel.SetMarginStart(8);
        panel.SetMarginEnd(8);
        panel.SetMarginTop(8);
        panel.SetMarginBottom(8);
        panel.Append(head);
        panel.Append(days);
        panel.Append(time);
        panel.Append(_errorInside);
        panel.Append(done);
        _popover.SetChild(panel);
        _popover.AddCssClass("glass");
        _popover.SetHasArrow(false);
        // The day that is picked has the focus
        _popover.OnShow += (_, _) => (_days.FirstOrDefault(d => d.GetActive() && d.GetVisible()) ?? _days.FirstOrDefault(d => d.GetSensitive() && d.GetVisible()))?.GrabFocus();
        Field.SetPopover(_popover);

        var label = Gtk.Label.NewWithMnemonic(l["studio.upload.scheduleStart"]);
        label.SetMnemonicWidget(Field);
        label.SetXalign(0);
        label.SetWrap(true);
        Root.Append(label);
        Root.Append(Field);
        Root.Append(_error);
        pick.Changed += (_, _) =>
        {
            picked();
            Render();
        };
        Render();
    }

    public Gtk.Box Root { get; } = Gtk.Box.New(Gtk.Orientation.Vertical, 4);

    private Gtk.MenuButton Field { get; } = Gtk.MenuButton.New();

    /// <summary>CI screenshots: the month, open.</summary>
    public void Open() => Field.Popup();

    /// <summary>CI's scenarios: the month is open, with one day picked.</summary>
    public bool IsOpen => _popover.GetVisible() && _days.Count(d => d.GetVisible() && d.GetActive()) == 1;

    /// <summary>The time passes too: a field that was right says when it no longer is.</summary>
    public void Render()
    {
        _rendering = true;
        _value.SetText(_pick.Label);
        _error.SetVisible(_pick.IsTooSoon);
        _errorInside.SetVisible(_pick.IsTooSoon);
        _month.SetText(_pick.MonthLabel);
        _back.SetSensitive(_pick.CanGoBack);
        _next.SetSensitive(_pick.CanGoNext);
        var days = _pick.Days;
        for (var i = 0; i < _days.Length; i++)
        {
            var at = i - _pick.LeadingBlanks;
            var day = at >= 0 && at < days.Count ? days[at] : null;
            var button = _days[i];
            button.SetVisible(day != null);
            button.SetSensitive(day is { IsEnabled: true });
            button.SetActive(day is { IsSelected: true });
            _labels[i].SetText(day?.Label ?? "");
            if (day == null) continue;
            _dates[i] = day.Date;
            Ui.Say(button, day.Name);
        }

        _hour.SetSelected((uint)_pick.Hour);
        _minute.SetSelected((uint)_pick.Minutes.ToList().IndexOf(_pick.Minute));
        _half.SetSelected(_pick.IsAfternoon ? 1u : 0u);
        _rendering = false;
    }

    private Gtk.Label Error()
    {
        var error = Ui.Label(_l.Format("studio.upload.scheduleTooSoon", (int)SchedulePick.Lead.TotalMinutes));
        error.AddCssClass("error");
        error.SetMaxWidthChars(34);
        return error;
    }

    private Gtk.Button Turn(string icon, string key, Action turn)
    {
        var button = Gtk.Button.NewFromIconName(icon);
        button.AddCssClass("flat");
        Ui.Say(Ui.Named(button, _l[key]), _l[key]);
        button.OnClicked += (_, _) => turn();
        return button;
    }

    private Gtk.DropDown Box(string key, IReadOnlyList<string> items, Action<int> set)
    {
        var box = Gtk.DropDown.NewFromStrings(items.ToArray());
        // What a list shows is its name unless a label has it: one that is not seen
        var name = Gtk.Label.New(_l[key]);
        name.SetVisible(false);
        name.SetMnemonicWidget(box);
        _names.Add(name);
        box.OnNotify += (_, e) =>
        {
            if (!_rendering && e.Pspec.GetName() == "selected") set((int)box.GetSelected());
        };
        return box;
    }
}
