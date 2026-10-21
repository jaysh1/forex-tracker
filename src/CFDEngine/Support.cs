using System.Globalization;
using System.Text.Json;

namespace CFDEngine;

public sealed class CfdBrokerException(string message) : Exception(message);
public sealed record CfdDecisionResult(bool Executed, string Message);
public sealed record CfdGuardStatus(bool CanTrade, string? Reason, decimal DayPnl, bool NewlyLocked = false);
public sealed record CfdGuardState(DateTime SessionStartUtc, decimal StartEquity, DateTime? LockedUntilUtc, string? LockReason);

public interface ICfdRiskStateStore
{
    string Name { get; }
    Task<string?> LoadAsync(CancellationToken ct);
    Task SaveAsync(string json, CancellationToken ct);
}

public sealed class CfdFileRiskStateStore(string path) : ICfdRiskStateStore
{
    public string Name => "local CFD file";

    public async Task<string?> LoadAsync(CancellationToken ct) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;

    public async Task SaveAsync(string json, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, ct);
        File.Move(temporaryPath, path, overwrite: true);
    }
}

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
    private readonly decimal _riskCapital;
    private bool _ready;
    private bool _dirty;

    public CfdDailyRiskGuard(decimal maxRiskPerTrade = CfdRiskLimits.DefaultMaxRiskPerTrade, bool awaitingRestore = true)
    {
        if (maxRiskPerTrade <= 0) throw new ArgumentOutOfRangeException(nameof(maxRiskPerTrade));
        _riskCapital = maxRiskPerTrade / (CfdRiskLimits.RiskPerTradePercent / 100m);
        _ready = !awaitingRestore;
    }

    public bool Ready { get { lock (_sync) return _ready; } }

    public static DateTime SessionStart(DateTime nowUtc) =>
        DateTime.SpecifyKind((nowUtc - Rollover).Date, DateTimeKind.Utc) + Rollover;

    public void Restore(CfdGuardState? state)
    {
        lock (_sync) { _state = state ?? new(DateTime.MinValue, 0, null, null); _ready = true; _dirty = false; }
    }

    public CfdGuardState? TakeDirtySnapshot()
    {
        lock (_sync) { if (!_dirty) return null; _dirty = false; return _state; }
    }

    public void MarkDirty() { lock (_sync) _dirty = true; }

    public CfdGuardStatus Evaluate(decimal equity, DateTime now)
    {
        lock (_sync)
        {
            if (!_ready) return new(false, "Waiting for CFD risk state to load; trading paused.", 0);

            var changed = false;
            var session = SessionStart(now);
            if (_state.StartEquity <= 0 || _state.SessionStartUtc != session)
            {
                _state = new(session, equity, null, null);
                changed = true;
            }
            if (_state.LockedUntilUtc is { } until && until <= now)
            {
                _state = _state with { LockedUntilUtc = null, LockReason = null };
                changed = true;
            }

            var pnl = equity - _state.StartEquity;
            var newlyLocked = false;
            if (_state.LockedUntilUtc is null && _state.StartEquity > 0)
            {
                var limitBase = Math.Min(_state.StartEquity, _riskCapital);
                var drawdown = limitBase * CfdRiskLimits.DailyDrawdownPercent / 100m;
                var target = limitBase * CfdRiskLimits.DailyProfitTargetPercent / 100m;
                if (pnl <= -drawdown)
                {
                    _state = _state with { LockedUntilUtc = now + CfdRiskLimits.DrawdownLockout,
                        LockReason = $"CFD daily drawdown cap hit (P&L {pnl:F2}, limit -{drawdown:F2}). Trading paused for 24h." };
                    newlyLocked = changed = true;
                }
                else if (pnl >= target)
                {
                    _state = _state with { LockedUntilUtc = session.AddHours(24),
                        LockReason = $"CFD daily profit target reached (P&L +{pnl:F2}, target +{target:F2}). Trading stopped until the next session." };
                    newlyLocked = changed = true;
                }
            }

            if (changed) _dirty = true;
            return new(_state.LockedUntilUtc is null, _state.LockReason, pnl, newlyLocked);
        }
    }
}

public sealed class CfdRiskPersistence(
    CfdDailyRiskGuard guard, ICfdRiskStateStore store, TimeProvider time, ILogger<CfdRiskPersistence> log,
    Action<string>? problemReported = null)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan RepeatProblemAfter = TimeSpan.FromMinutes(10);
    private string? _lastProblem;
    private DateTime _lastProblemAtUtc = DateTime.MinValue;
    public string? LastProblem => _lastProblem;

    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (guard.Ready) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (guard.Ready) return;
            try
            {
                var json = await store.LoadAsync(ct);
                var state = json is null ? null : JsonSerializer.Deserialize<CfdGuardState>(json);
                if (json is not null && state is null) throw new JsonException("saved CFD risk state was empty");
                if (state is not null) Validate(state, time.GetUtcNow().UtcDateTime);
                guard.Restore(state);
                _lastProblem = null;
                log.LogInformation("Loaded CFD risk state from {StateStore}", store.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report($"Cannot load the saved CFD risk state from {store.Name}: {ex.Message}. CFD trading stays paused.");
            }
        }
        finally { _lock.Release(); }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (!guard.Ready) return;
        await _lock.WaitAsync(ct);
        try
        {
            var state = guard.TakeDirtySnapshot();
            if (state is null) return;

            try
            {
                await store.SaveAsync(JsonSerializer.Serialize(state), ct);
                _lastProblem = null;
            }
            catch (OperationCanceledException)
            {
                guard.MarkDirty();
                throw;
            }
            catch (Exception ex)
            {
                guard.MarkDirty();
                Report($"Could not save the CFD risk state to {store.Name}: {ex.Message}. Will retry; a restart could lose the current lockout.");
            }
        }
        finally { _lock.Release(); }
    }

    private static void Validate(CfdGuardState state, DateTime nowUtc)
    {
        if (state.StartEquity <= 0 || state.SessionStartUtc == default || state.SessionStartUtc.Kind != DateTimeKind.Utc ||
            state.SessionStartUtc != CfdDailyRiskGuard.SessionStart(state.SessionStartUtc) ||
            state.SessionStartUtc > CfdDailyRiskGuard.SessionStart(nowUtc) ||
            (state.LockedUntilUtc is { } until && (until.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(state.LockReason))))
            throw new JsonException("saved CFD risk state is invalid");
    }

    private void Report(string message)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (message == _lastProblem && now - _lastProblemAtUtc < RepeatProblemAfter) return;
        _lastProblem = message;
        _lastProblemAtUtc = now;
        log.LogWarning("{Message}", message);
        problemReported?.Invoke(message);
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
