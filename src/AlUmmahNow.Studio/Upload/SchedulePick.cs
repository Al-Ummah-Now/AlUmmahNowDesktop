using AlUmmahNow.App.I18n;

namespace AlUmmahNow.Studio.Upload;

/// <summary>A day of the month that is shown.</summary>
public sealed record PickDay(DateTime Date, string Label, string Name, bool IsSelected, bool IsEnabled);

/// <summary>
/// The date and time a first video publishes at: a Gregorian month of days and a 12-hour time, in the interface's
/// language and digits. Both shells draw it: the toolkits' own calendars follow the system's language and calendar.
/// </summary>
public sealed class SchedulePick
{
    /// <summary>As on the web: room for the upload.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(15);

    /// <summary>As on the web too: nothing is planned later than this.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromDays(365);

    private readonly Localizer _l;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeZoneInfo _zone;
    private DateTime _local;

    public SchedulePick(Localizer localizer, DateTimeOffset value, Func<DateTimeOffset> now, TimeZoneInfo? zone = null)
    {
        _l = localizer;
        _now = now;
        _zone = zone ?? TimeZoneInfo.Local;
        _local = Real(TimeZoneInfo.ConvertTime(value, _zone).DateTime);
        Month = new DateTime(_local.Year, _local.Month, 1);
        Minutes = Enumerable.Range(0, 12).Select(i => i * 5).Append(_local.Minute).Distinct().OrderBy(m => m).ToList();
    }

    /// <summary>What a schedule begins with: tomorrow at 9:00.</summary>
    public static DateTimeOffset TomorrowMorning(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var at = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1).AddHours(9);
        return new DateTimeOffset(at, zone.GetUtcOffset(at));
    }

    public event EventHandler? Changed;

    /// <summary>The first day of the month that is shown.</summary>
    public DateTime Month { get; private set; }

    public DateTimeOffset Value => new(_local, _zone.GetUtcOffset(_local));

    /// <summary>An hour the clocks skip is the one after it, in the lists too.</summary>
    private DateTime Real(DateTime local)
    {
        var at = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return _zone.IsInvalidTime(at) ? at.AddHours(1) : at;
    }

    /// <summary>Too soon, or past: no upload starts with it.</summary>
    public bool IsTooSoon => IsSoon(Value, _now());

    public static bool IsSoon(DateTimeOffset value, DateTimeOffset now) => value < now + Lead;

    public string Label => _l.DateTime(Value);

    public string MonthLabel => _l.MonthYear(Month);

    /// <summary>The week begins on Sunday, in Arabic on Saturday.</summary>
    public IReadOnlyList<(string Label, string Name)> Weekdays =>
        Enumerable.Range(0, 7).Select(i => (DayOfWeek)((First + i) % 7)).Select(d => (_l.WeekdayLetter(d), _l.Weekday(d))).ToList();

    private int First => _l.Language == "ar" ? 6 : 0;

    /// <summary>Where the first of the month is in its week.</summary>
    public int LeadingBlanks => ((int)Month.DayOfWeek - First + 7) % 7;

    public IReadOnlyList<PickDay> Days
    {
        get
        {
            return Enumerable.Range(0, DateTime.DaysInMonth(Month.Year, Month.Month)).Select(i => Month.AddDays(i))
                .Select(d => new PickDay(d, _l.DayOfMonth(d), _l.LongDay(d), d == _local.Date, d >= FirstDay && d <= LastDay)).ToList();
        }
    }

    /// <summary>The first day that has a time which is not too soon.</summary>
    private DateTime FirstDay => TimeZoneInfo.ConvertTime(_now() + Lead, _zone).Date;

    /// <summary>The month is opened: the one of the time that is picked, or where a day can be picked.</summary>
    public void Open()
    {
        var day = _local < FirstDay ? FirstDay : _local;
        Month = new DateTime(day.Year, day.Month, 1);
    }

    public bool CanGoBack => Month > FirstDay;

    public void Back()
    {
        if (CanGoBack) Show(Month.AddMonths(-1));
    }

    public bool CanGoNext => Month.AddMonths(1) <= LastDay;

    /// <summary>The last day whose every time is soon enough.</summary>
    private DateTime LastDay => TimeZoneInfo.ConvertTime(_now() + Longest, _zone).Date.AddDays(-1);

    public void Next()
    {
        if (CanGoNext) Show(Month.AddMonths(1));
    }

    private void Show(DateTime month)
    {
        Month = month;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Pick(DateTime day) => Set(day.Date + _local.TimeOfDay);

    /// <summary>"12", "1", … "11".</summary>
    public IReadOnlyList<string> Hours => Enumerable.Range(0, 12).Select(h => _l.Number(h == 0 ? 12 : h)).ToList();

    public int Hour
    {
        get => _local.Hour % 12;
        set => Set(_local.Date.AddHours((_local.Hour / 12 * 12) + value).AddMinutes(_local.Minute));
    }

    /// <summary>Every five minutes, and the one it began with.</summary>
    public IReadOnlyList<int> Minutes { get; }

    public IReadOnlyList<string> MinuteLabels => Minutes.Select(_l.Minute).ToList();

    public int Minute
    {
        get => _local.Minute;
        set => Set(_local.Date.AddHours(_local.Hour).AddMinutes(value));
    }

    public IReadOnlyList<string> Halves => new[] { _l.DayHalf(false), _l.DayHalf(true) };

    public bool IsAfternoon
    {
        get => _local.Hour >= 12;
        set => Set(_local.Date.AddHours((_local.Hour % 12) + (value ? 12 : 0)).AddMinutes(_local.Minute));
    }

    private void Set(DateTime local)
    {
        // The lists change back when what was chosen is not a time, so this is said even if nothing else changes
        var real = Real(local);
        if (real == _local && real == local) return;
        _local = real;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
