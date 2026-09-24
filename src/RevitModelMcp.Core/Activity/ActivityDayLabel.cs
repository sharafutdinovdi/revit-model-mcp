using System.Globalization;

namespace RevitModelMcp.Core.Activity;

/// <summary>Day group label of an activity row, from the local calendar dates of the entry and of now.</summary>
public static class ActivityDayLabel
{
    public static string For(DateTimeOffset entryTime, DateTimeOffset now) => For(entryTime, now, TimeZoneInfo.Local);

    /// <summary>
    /// "Today", "Yesterday", a weekday name within the last 6 days, then "Sep 21" in the current year or
    /// "Sep 21, 2025" in earlier years. An entry later than <paramref name="now"/> (clock skew) reads "Today".
    /// </summary>
    public static string For(DateTimeOffset entryTime, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (zone is null) throw new ArgumentNullException(nameof(zone));
        var entryDate = TimeZoneInfo.ConvertTime(entryTime, zone).Date;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        // Both are midnight dates with no offset, so the difference counts calendar days even across DST.
        var days = (today - entryDate).Days;
        if (days <= 0) return "Today";
        if (days == 1) return "Yesterday";
        if (days <= 6) return entryDate.ToString("dddd", CultureInfo.InvariantCulture);
        return entryDate.ToString(entryDate.Year == today.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture);
    }
}
