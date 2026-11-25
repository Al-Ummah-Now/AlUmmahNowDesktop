using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Upload;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class SchedulePickTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 17, 0, 0, TimeSpan.Zero); // 12:00 PM there

    private static SchedulePick At(string language, DateTimeOffset value) => new(new Localizer(language, NewYork), value, () => Now, NewYork);

    private static SchedulePick Tomorrow(string language = "en") => At(language, new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("en", "November 2026", 0, "Sun", "Sunday", "22", "Sunday, November 22, 2026")]
    [InlineData("ar", "نوفمبر ٢٠٢٦", 1, "س", "السبت", "٢٢", "الأحد، ٢٢ نوفمبر ٢٠٢٦")]
    public void A_month_is_in_the_language(string language, string month, int blanks, string letter, string weekday, string day, string name)
    {
        var pick = Tomorrow(language);
        Assert.Equal(month, pick.MonthLabel);
        Assert.Equal(blanks, pick.LeadingBlanks);
        Assert.Equal((letter, weekday), pick.Weekdays[0]);
        Assert.Equal(7, pick.Weekdays.Distinct().Count());
        Assert.Equal(30, pick.Days.Count);
        var selected = Assert.Single(pick.Days, d => d.IsSelected);
        Assert.Equal((day, name), (selected.Label, selected.Name));
        Assert.DoesNotMatch(language == "ar" ? "[0-9]" : "[٠-٩]", pick.Label + string.Concat(pick.Hours) + string.Concat(pick.MinuteLabels) + string.Concat(pick.Days.Select(d => d.Label + d.Name)));
    }

    [Fact]
    public void Days_that_are_past_cannot_be_picked()
    {
        var pick = Tomorrow();
        Assert.Equal(new DateTime(2026, 11, 21), pick.Days.First(d => d.IsEnabled).Date);
        Assert.False(pick.CanGoBack);
        pick.Back();
        Assert.Equal(11, pick.Month.Month);
        pick.Next();
        Assert.Equal("December 2026", pick.MonthLabel);
        Assert.All(pick.Days, d => Assert.True(d.IsEnabled));
        Assert.DoesNotContain(pick.Days, d => d.IsSelected);
        pick.Back();
        Assert.Equal(11, pick.Month.Month);
        // The last quarter of an hour of a day is too late for it
        var late = new SchedulePick(new Localizer("en", NewYork), Now.AddDays(1), () => new DateTimeOffset(2026, 11, 22, 4, 50, 0, TimeSpan.Zero), NewYork);
        Assert.Equal(new DateTime(2026, 11, 22), late.Days.First(d => d.IsEnabled).Date);
    }

    [Fact]
    public void A_month_without_a_day_left_is_not_gone_back_to()
    {
        // 11:50 PM on November 30 there: November's last quarter of an hour
        var pick = new SchedulePick(new Localizer("en", NewYork), new DateTimeOffset(2026, 12, 1, 14, 0, 0, TimeSpan.Zero), () => new DateTimeOffset(2026, 12, 1, 4, 50, 0, TimeSpan.Zero), NewYork);
        Assert.Equal(12, pick.Month.Month);
        Assert.False(pick.CanGoBack);
        pick.Back();
        Assert.Equal(12, pick.Month.Month);
        Assert.Contains(pick.Days, d => d.IsEnabled);
    }

    [Fact]
    public void Opened_later_it_is_of_the_time_that_is_now()
    {
        var now = Now;
        var pick = new SchedulePick(new Localizer("en", NewYork), Now.AddMinutes(20), () => now, NewYork);
        Assert.False(pick.IsTooSoon);
        now = Now.AddMinutes(6);
        Assert.True(pick.IsTooSoon);
        // Another month that was looked at is not the one it opens with
        pick.Next();
        pick.Open();
        Assert.Equal(new DateTime(2026, 11, 1), pick.Month);
        // After midnight the day before cannot be picked; after the month, the month that is now is shown
        now = Now.AddDays(1);
        Assert.Equal(new DateTime(2026, 11, 22), pick.Days.First(d => d.IsEnabled).Date);
        now = Now.AddDays(20);
        pick.Open();
        Assert.Equal(new DateTime(2026, 12, 11), pick.Days.First(d => d.IsEnabled).Date);
        Assert.DoesNotContain(pick.Days, d => d.IsSelected);
    }

    [Fact]
    public void Nothing_is_planned_later_than_a_year()
    {
        var pick = Tomorrow();
        for (var i = 0; i < 12; i++) pick.Next();
        Assert.Equal(new DateTime(2027, 11, 1), pick.Month);
        Assert.Equal(new DateTime(2027, 11, 20), pick.Days.Last(d => d.IsEnabled).Date);
        Assert.False(pick.CanGoNext);
        pick.Next();
        Assert.Equal(new DateTime(2027, 11, 1), pick.Month);
        pick.Back();
        Assert.True(pick.CanGoNext);
        // On the first of a month, the month a year later has no day of its own left
        var first = new SchedulePick(new Localizer("en", NewYork), Now, () => new DateTimeOffset(2026, 12, 1, 17, 0, 0, TimeSpan.Zero), NewYork);
        for (var i = 0; i < 20; i++) first.Next();
        Assert.Equal(new DateTime(2027, 11, 1), first.Month);
    }

    [Fact]
    public void The_parts_make_the_time()
    {
        var pick = Tomorrow();
        var changes = 0;
        pick.Changed += (_, _) => changes++;
        Assert.Equal((9, 0, false), (pick.Hour, pick.Minute, pick.IsAfternoon));
        pick.Hour = 0;
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.FromHours(-5)), pick.Value);
        pick.IsAfternoon = true;
        pick.Minute = 30;
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 12, 30, 0, TimeSpan.FromHours(-5)), pick.Value);
        pick.Pick(new DateTime(2026, 12, 1));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 12, 30, 0, TimeSpan.FromHours(-5)), pick.Value);
        pick.Minute = 30;
        Assert.Equal(4, changes);
        Assert.Equal("12", pick.Hours[0]);
        Assert.Equal("11", pick.Hours[11]);
    }

    [Fact]
    public void A_time_between_the_fives_is_kept()
    {
        var pick = At("en", new DateTimeOffset(2026, 11, 22, 14, 7, 0, TimeSpan.Zero));
        Assert.Contains(7, pick.Minutes);
        Assert.Equal(13, pick.Minutes.Count);
        pick.Minute = 10;
        Assert.Equal(10, pick.Minute);
        // The list one picks from does not change under them
        Assert.Contains(7, pick.Minutes);
    }

    [Fact]
    public void A_schedule_begins_with_tomorrow_morning() =>
        // 12:30 AM on November 22 there, and still November 21 elsewhere
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.FromHours(-5)), SchedulePick.TomorrowMorning(new DateTimeOffset(2026, 11, 21, 21, 30, 0, TimeSpan.FromHours(-8)), NewYork));

    [Fact]
    public void A_quarter_of_an_hour_is_needed()
    {
        Assert.True(At("en", Now).IsTooSoon);
        Assert.True(At("en", Now.AddMinutes(14)).IsTooSoon);
        Assert.False(At("en", Now.AddMinutes(15)).IsTooSoon);
    }

    /// <summary>In New York the clocks skip 2:00 to 3:00 on March 14, 2027, and have 1:00 to 2:00 twice on November 7.</summary>
    [Fact]
    public void Hours_the_clocks_skip_or_have_twice_are_times()
    {
        var pick = At("en", new DateTimeOffset(2027, 3, 13, 7, 30, 0, TimeSpan.Zero));
        pick.Pick(new DateTime(2027, 3, 14));
        Assert.Equal(new DateTimeOffset(2027, 3, 14, 3, 30, 0, TimeSpan.FromHours(-4)), pick.Value);
        // The lists and the label say the same, and the next day keeps it
        Assert.Equal((3, 30, false), (pick.Hour, pick.Minute, pick.IsAfternoon));
        Assert.Contains("3:30", pick.Label);
        // The hour is not there to choose: the list is told to go back
        var said = 0;
        pick.Changed += (_, _) => said++;
        pick.Hour = 2;
        Assert.Equal((3, 1), (pick.Hour, said));
        pick.Hour = 3;
        Assert.Equal(1, said);
        pick.Pick(new DateTime(2027, 3, 15));
        Assert.Equal(new DateTimeOffset(2027, 3, 15, 3, 30, 0, TimeSpan.FromHours(-4)), pick.Value);
        Assert.Equal(3, At("en", new DateTimeOffset(2027, 3, 14, 7, 30, 0, TimeSpan.Zero)).Hour);
        pick.Pick(new DateTime(2027, 11, 7));
        pick.Hour = 1;
        Assert.Equal(new DateTime(2027, 11, 7, 1, 30, 0), pick.Value.DateTime);
    }
}
