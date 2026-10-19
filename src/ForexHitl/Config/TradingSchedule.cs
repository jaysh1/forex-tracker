using System.Globalization;

namespace ForexHitl;

/// <summary>Local wall-clock hours, with the zone's daylight-saving rules applied on every check.</summary>
public sealed class TradingSchedule
{
    private readonly TimeZoneInfo _zone;
    private readonly HashSet<DayOfWeek> _days;
    public TimeOnly Start { get; }
    public TimeOnly End { get; }
    public string Description { get; }
    public string ClosedReason => $"Outside trading hours: {Description}.";

    public TradingSchedule(string zone = "Australia/Melbourne", string start = "09:00", string end = "17:00",
        string days = "Monday,Tuesday,Wednesday,Thursday,Friday")
    {
        _zone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        if (!TimeOnly.TryParseExact(start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !TimeOnly.TryParseExact(end, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || from >= to)
            throw new InvalidOperationException("Trading hours require HH:mm times with start before end (same day).");
        Start = from;
        End = to;
        var names = days.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _days = new HashSet<DayOfWeek>();
        foreach (var name in names)
        {
            if (!Enum.TryParse<DayOfWeek>(name, true, out var day) || !Enum.IsDefined(day) || char.IsDigit(name[0]))
                throw new InvalidOperationException("TRADING_DAYS must contain day names such as Monday,Tuesday.");
            _days.Add(day);
        }
        if (_days.Count == 0) throw new InvalidOperationException("TRADING_DAYS cannot be empty.");
        Description = $"{string.Join(", ", _days.OrderBy(d => ((int)d + 6) % 7))}, {start}–{end} ({_zone.Id})";
    }

    public bool IsOpen(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);
        var time = TimeOnly.FromDateTime(local);
        return _days.Contains(local.DayOfWeek) && time >= Start && time < End;
    }
}
