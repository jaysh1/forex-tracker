using System.Globalization;
using System.Text.Json;

namespace CFDEngine;

public sealed class CfdBrokerException(string message) : Exception(message);
public sealed record CfdDecisionResult(bool Executed, string Message);
public sealed record CfdGuardStatus(bool CanTrade, string? Reason, decimal DayPnl);
public sealed record CfdGuardState(DateTime SessionStartUtc, decimal StartEquity, DateTime? LockedUntilUtc, string? LockReason);

/// <summary>CFD-owned schedule parser. It deliberately has no dependency on Forex market-hours code.</summary>
public sealed class CfdTradingWindow
{
    private readonly TimeZoneInfo? _zone;
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;
    private readonly HashSet<DayOfWeek> _days;
    private CfdTradingWindow(TimeZoneInfo? zone, TimeOnly start, TimeOnly end, HashSet<DayOfWeek> days)
    { _zone = zone; _start = start; _end = end; _days = days; }
    public static CfdTradingWindow Parse(string hours, string days, string? zoneId = null)
    {
        hours = string.IsNullOrWhiteSpace(hours) ? "09:00-17:00" : hours.Trim();
        if (hours.Equals("always", StringComparison.OrdinalIgnoreCase) || hours.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            hours.Equals("none", StringComparison.OrdinalIgnoreCase) || hours.Equals("24/7", StringComparison.OrdinalIgnoreCase))
            return new CfdTradingWindow(null, TimeOnly.MinValue, TimeOnly.MaxValue, new HashSet<DayOfWeek>());
        var timeParts = hours.Split('-', StringSplitOptions.TrimEntries);
        if (timeParts.Length != 2 || !TimeOnly.TryParse(timeParts[0], CultureInfo.InvariantCulture, out var start) ||
            !TimeOnly.TryParse(timeParts[1], CultureInfo.InvariantCulture, out var end) || start >= end)
            throw new InvalidOperationException($"CFD_TRADING_HOURS '{hours}' must be HH:mm-HH:mm or 'always', with start before end.");
        var parsed = new HashSet<DayOfWeek>();
        foreach (var item in days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = item.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 2)
            {
                var first = ParseDay(range[0]); var last = ParseDay(range[1]);
                for (var d = (int)first; ; d = (d + 1) % 7) { parsed.Add((DayOfWeek)d); if (d == (int)last) break; }
            }
            else parsed.Add(ParseDay(item));
        }
        if (parsed.Count == 0) throw new InvalidOperationException("CFD_TRADING_DAYS must list at least one day.");
        var zoneName = string.IsNullOrWhiteSpace(zoneId) ? "Australia/Melbourne" : zoneId.Trim();
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidOperationException($"CFD_TRADING_TIMEZONE '{zoneName}' was not found."); }
        return new CfdTradingWindow(zone, start, end, parsed);
    }
    private static DayOfWeek ParseDay(string value)
    {
        var normalized = value.Length >= 3 ? value[..3] : value;
        foreach (var day in Enum.GetValues<DayOfWeek>())
            if (day.ToString().StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return day;
        throw new InvalidOperationException($"Invalid CFD trading day '{value}'.");
    }
    public bool IsOpen(DateTime utc)
    {
        if (_zone is null) return true;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);
        return _days.Contains(local.DayOfWeek) && TimeOnly.FromDateTime(local) >= _start && TimeOnly.FromDateTime(local) < _end;
    }
    public string Describe() => _zone is null ? "any time" : $"{string.Join(",", _days)} {_start:HH:mm}-{_end:HH:mm} {_zone.Id}";
}

/// <summary>CFD-owned daily equity guard (1.5% drawdown / 1% profit lock), persisted independently.</summary>
public sealed class CfdDailyRiskGuard
{
    private static readonly TimeSpan Rollover = TimeSpan.FromHours(22);
    private readonly object _sync = new();
    private CfdGuardState _state = new(DateTime.MinValue, 0, null, null);
    private bool _ready;
    private bool _dirty;
    public void Restore(CfdGuardState? state) { lock (_sync) { _state = state ?? new(DateTime.MinValue, 0, null, null); _ready = true; _dirty = false; } }
    public CfdGuardState? TakeDirtySnapshot() { lock (_sync) { if (!_dirty) return null; _dirty = false; return _state; } }
    public void MarkDirty() { lock (_sync) _dirty = true; }
    public CfdGuardStatus Evaluate(decimal equity, DateTime now)
    {
        lock (_sync)
        {
            if (!_ready) return new(false, "Waiting for CFD risk state to load; trading paused.", 0);
            var session = DateTime.SpecifyKind((now - Rollover).Date, DateTimeKind.Utc) + Rollover;
            if (_state.StartEquity <= 0 || _state.SessionStartUtc != session) _state = new(session, equity, null, null);
            if (_state.LockedUntilUtc is { } until && until <= now) _state = _state with { LockedUntilUtc = null, LockReason = null };
            var pnl = equity - _state.StartEquity;
            if (_state.LockedUntilUtc is null && _state.StartEquity > 0)
            {
                var drawdown = _state.StartEquity * 0.015m;
                var target = _state.StartEquity * 0.01m;
                if (pnl <= -drawdown) _state = _state with { LockedUntilUtc = now.AddHours(24), LockReason = $"CFD daily drawdown limit reached ({pnl:F2})." };
                else if (pnl >= target) _state = _state with { LockedUntilUtc = session.AddHours(24), LockReason = $"CFD daily profit target reached ({pnl:F2})." };
            }
            _dirty = true;
            return new(_state.LockedUntilUtc is null, _state.LockReason, pnl);
        }
    }
}

public sealed class CfdRiskPersistence(CfdDailyRiskGuard guard, string path)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;
    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            CfdGuardState? state = null;
            if (File.Exists(path))
                state = JsonSerializer.Deserialize<CfdGuardState>(await File.ReadAllTextAsync(path, ct));
            guard.Restore(state);
            _loaded = true;
        }
        finally { _lock.Release(); }
    }
    public async Task FlushAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = guard.TakeDirtySnapshot();
            if (state is null) return;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state), ct);
        }
        catch { guard.MarkDirty(); throw; }
        finally { _lock.Release(); }
    }
}

internal static class CfdIndicators
{
    public static decimal?[] Ema(IReadOnlyList<decimal> values, int period)
    {
        var result = new decimal?[values.Count]; if (period < 1 || values.Count < period) return result;
        var ema = values.Take(period).Sum() / period; result[period - 1] = ema; var k = 2m / (period + 1);
        for (var i = period; i < values.Count; i++) { ema += (values[i] - ema) * k; result[i] = ema; }
        return result;
    }
    public static decimal?[] Rsi(IReadOnlyList<decimal> values, int period)
    {
        var result = new decimal?[values.Count]; if (period < 1 || values.Count <= period) return result;
        decimal gain = 0, loss = 0;
        for (var i = 1; i <= period; i++) { var d = values[i] - values[i - 1]; if (d > 0) gain += d; else loss -= d; }
        var ag = gain / period; var al = loss / period; result[period] = RsiValue(ag, al);
        for (var i = period + 1; i < values.Count; i++) { var d = values[i] - values[i - 1]; ag = (ag * (period - 1) + Math.Max(d, 0)) / period; al = (al * (period - 1) + Math.Max(-d, 0)) / period; result[i] = RsiValue(ag, al); }
        return result;
    }
    public static decimal?[] Atr(IReadOnlyList<CfdCandle> candles, int period)
    {
        var result = new decimal?[candles.Count]; if (period < 1 || candles.Count <= period) return result;
        var tr = new decimal[candles.Count];
        for (var i = 1; i < candles.Count; i++) tr[i] = Math.Max(candles[i].High-candles[i].Low, Math.Max(Math.Abs(candles[i].High-candles[i-1].Close), Math.Abs(candles[i].Low-candles[i-1].Close)));
        var atr = tr.Skip(1).Take(period).Sum() / period; result[period] = atr;
        for (var i = period + 1; i < candles.Count; i++) { atr = (atr * (period - 1) + tr[i]) / period; result[i] = atr; }
        return result;
    }
    private static decimal RsiValue(decimal gain, decimal loss) => loss == 0 ? (gain == 0 ? 50 : 100) : 100 - 100 / (1 + gain / loss);
}
