using System.Globalization;

namespace OpenClaw.Agent;

internal static class AgentPromptDateTime
{
    public static string ResolveTimezone()
    {
        var id = TimeZoneInfo.Local.Id;
        return string.IsNullOrWhiteSpace(id) ? "UTC" : id;
    }

    public static string FormatCurrentTime(string timezone)
    {
        var now = DateTimeOffset.UtcNow;
        var tz = TryFindTimezone(timezone);
        var local = tz is not null ? TimeZoneInfo.ConvertTime(now, tz) : now;
        var formatted = FormatLongDateTime(local);
        var utc = now.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        return $"{formatted} ({timezone}) / {utc}";
    }

    private static TimeZoneInfo? TryFindTimezone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return null; }
    }

    private static string FormatLongDateTime(DateTimeOffset dt)
    {
        var day = dt.Day;
        var suffix = OrdinalSuffix(day);
        var use24h = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.StartsWith('H');
        var timePart = use24h
            ? dt.ToString("HH:mm", CultureInfo.InvariantCulture)
            : dt.ToString("h:mm tt", CultureInfo.CurrentCulture);
        return $"{dt.ToString("dddd, MMMM", CultureInfo.InvariantCulture)} {day}{suffix}, {dt.Year} - {timePart}";
    }

    private static string OrdinalSuffix(int day) => (day % 100) switch
    {
        11 or 12 or 13 => "th",
        _ => (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" }
    };
}
