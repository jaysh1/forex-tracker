using System.Globalization;

namespace ForexHitl;

/// <summary>
/// Weekly trading hours in a named time zone (default: Monday-Friday 09:00-17:00 Australia/Melbourne).
/// Daylight saving is handled by the time zone database. The window start is inclusive and the end is
/// exclusive, and a window cannot cross midnight.
/// </summary>
public sealed class TradingWindow
{
    public const string DefaultHours = "09:00-17:00";
    public const string DefaultDays = "Mon-Fri";
    public const string DefaultZone = "Australia/Melbourne";

    private static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    private readonly TimeZoneInfo? _zone;
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;
    private readonly HashSet<DayOfWeek> _days;

    private TradingWindow(TimeZoneInfo? zone, TimeOnly start, TimeOnly end, HashSet<DayOfWeek> days)
    {
        _zone = zone;
        _start = start;
        _end = end;
        _days = days;
    }

    /// <summary>No restriction: trading is allowed at any time (the market's own hours still apply).</summary>
    public static TradingWindow Always { get; } = new(null, TimeOnly.MinValue, TimeOnly.MaxValue, new HashSet<DayOfWeek>());

    public bool Unrestricted => _zone is null;
    public TimeZoneInfo? Zone => _zone;

    public bool IsOpen(DateTime utcNow)
    {
        if (_zone is null) return true;

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), _zone);
        var time = TimeOnly.FromDateTime(local);
        return _days.Contains(local.DayOfWeek) && time >= _start && time < _end;
    }

    /// <summary>Human-readable, e.g. "Mon-Fri 09:00-17:00 Australia/Melbourne".</summary>
    public string Describe() => _zone is null
        ? "any time"
        : $"{DescribeDays()} {_start:HH\\:mm}-{_end:HH\\:mm} {_zone.Id}";

    public static TradingWindow Parse(string? hours, string? days, string? zoneId)
    {
        hours = string.IsNullOrWhiteSpace(hours) ? DefaultHours : hours.Trim();
        if (hours.ToLowerInvariant() is "always" or "off" or "none" or "24/7")
            return Always;

        var parts = hours.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TryTime(parts[0], out var start) || !TryTime(parts[1], out var end))
            throw new InvalidOperationException(
                $"TRADING_HOURS '{hours}' is not valid. Use HH:mm-HH:mm (for example 09:00-17:00) or 'always'.");
        if (start >= end)
            throw new InvalidOperationException(
                $"TRADING_HOURS '{hours}' must start before it ends (windows that cross midnight are not supported).");

        var dayText = string.IsNullOrWhiteSpace(days) ? DefaultDays : days.Trim();
        var daySet = ParseDays(dayText);

        var zone = string.IsNullOrWhiteSpace(zoneId) ? DefaultZone : zoneId.Trim();
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"TRADING_TIMEZONE '{zone}' was not found on this machine. Use an IANA name such as Australia/Melbourne " +
                "(a minimal container image may also be missing its tzdata package).");
        }

        return new TradingWindow(tz, start, end, daySet);
    }

    private static bool TryTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static HashSet<DayOfWeek> ParseDays(string text)
    {
        var set = new HashSet<DayOfWeek>();
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = token.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is < 1 or > 2 || !TryDay(range[0], out var from) || !TryDay(range[^1], out var to))
                throw new InvalidOperationException(
                    $"TRADING_DAYS '{text}' is not valid. Use day names such as Mon-Fri or Mon,Wed,Fri.");

            // Index 0 = Monday. A range may wrap past Sunday (Fri-Mon).
            for (var i = from; ; i = (i + 1) % 7)
            {
                set.Add(ToDayOfWeek(i));
                if (i == to) break;
            }
        }

        if (set.Count == 0)
            throw new InvalidOperationException("TRADING_DAYS must list at least one day.");
        return set;
    }

    private static bool TryDay(string text, out int index)
    {
        index = text.Length >= 3
            ? Array.FindIndex(DayNames, d => d.Equals(text[..3], StringComparison.OrdinalIgnoreCase))
            : -1;
        return index >= 0;
    }

    private static DayOfWeek ToDayOfWeek(int mondayIndex) => (DayOfWeek)((mondayIndex + 1) % 7);

    private string DescribeDays()
    {
        var present = Enumerable.Range(0, 7).Select(i => _days.Contains(ToDayOfWeek(i))).ToArray();
        var runs = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            if (!present[i]) continue;
            var j = i;
            while (j + 1 < 7 && present[j + 1]) j++;
            runs.Add(j - i >= 2 ? $"{DayNames[i]}-{DayNames[j]}" : string.Join(",", DayNames[i..(j + 1)]));
            i = j;
        }
        return string.Join(",", runs);
    }
}
