using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Core.Tests.Activity;

public sealed class ActivityDayLabelTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Berlin = FindBerlin();

    [Test]
    [Arguments("2026-09-24T00:00:00", "Today")]
    [Arguments("2026-09-24T09:59:59", "Today")]
    [Arguments("2026-09-23T23:59:59", "Yesterday")]
    [Arguments("2026-09-23T00:00:00", "Yesterday")]
    [Arguments("2026-09-22T23:59:59", "Tuesday")]
    [Arguments("2026-09-18T12:00:00", "Friday")]
    [Arguments("2026-09-17T23:59:59", "Sep 17")]
    [Arguments("2026-01-01T00:00:00", "Jan 1")]
    [Arguments("2025-12-31T23:59:59", "Dec 31, 2025")]
    public async Task For_UsesCalendarDates(string entry, string expected)
    {
        var now = At("2026-09-24T10:00:00", Utc);
        await Assert.That(ActivityDayLabel.For(At(entry, Utc), now, Utc)).IsEqualTo(expected);
    }

    [Test]
    public async Task For_JustAfterMidnight_PreviousEveningIsYesterday()
    {
        var now = At("2026-09-24T00:00:01", Utc);
        await Assert.That(ActivityDayLabel.For(At("2026-09-23T23:59:59", Utc), now, Utc)).IsEqualTo("Yesterday");
        await Assert.That(ActivityDayLabel.For(At("2026-09-24T00:00:00", Utc), now, Utc)).IsEqualTo("Today");
    }

    [Test]
    public async Task For_EntryLaterThanNow_IsToday()
    {
        var now = At("2026-09-24T23:59:00", Utc);
        await Assert.That(ActivityDayLabel.For(At("2026-09-25T00:30:00", Utc), now, Utc)).IsEqualTo("Today");
    }

    [Test]
    public async Task For_ConvertsBothTimesToTheLocalZone()
    {
        // 22:30 UTC on the 23rd is 00:30 on the 24th in Berlin (UTC+2 in September).
        var entry = new DateTimeOffset(2026, 9, 23, 22, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        await Assert.That(ActivityDayLabel.For(entry, now, Berlin)).IsEqualTo("Today");
        await Assert.That(ActivityDayLabel.For(entry, now, Utc)).IsEqualTo("Yesterday");
    }

    [Test]
    public async Task For_FallBackDayLongerThan24Hours_StaysToday()
    {
        // 25 October 2026 has 25 hours in Berlin: 00:10 (+02:00) to 23:50 (+01:00) is 24 h 40 min apart.
        var entry = new DateTimeOffset(2026, 10, 25, 0, 10, 0, TimeSpan.FromHours(2));
        var now = new DateTimeOffset(2026, 10, 25, 23, 50, 0, TimeSpan.FromHours(1));
        await Assert.That(ActivityDayLabel.For(entry, now, Berlin)).IsEqualTo("Today");
    }

    [Test]
    public async Task For_SpringForwardNight_LessThan24HoursIsYesterday()
    {
        // 29 March 2026 has 23 hours in Berlin: 23:30 on the 28th (+01:00) to 23:00 on the 29th (+02:00) is 22 h 30 min.
        var entry = new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.FromHours(1));
        var now = new DateTimeOffset(2026, 3, 29, 23, 0, 0, TimeSpan.FromHours(2));
        await Assert.That(ActivityDayLabel.For(entry, now, Berlin)).IsEqualTo("Yesterday");
    }

    private static DateTimeOffset At(string local, TimeZoneInfo zone)
    {
        var time = DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture);
        return new DateTimeOffset(time, zone.GetUtcOffset(time));
    }

    private static TimeZoneInfo FindBerlin()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
    }
}
