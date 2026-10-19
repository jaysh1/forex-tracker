using System.Globalization;
using System.Text.Json;

namespace ForexHitl;

public sealed record GuardStatus(bool CanTrade, string? Reason, decimal DayPnl, bool NewlyLocked);

public sealed class GuardState
{
    public DateTime SessionStartUtc { get; set; }
    public decimal StartEquity { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LockReason { get; set; }
}

/// <summary>
/// Enforces the daily drawdown cap (1.5% -> 24h lockout) and daily profit cap (1.0% -> stop until the
/// next session). P&amp;L is measured on equity (NAV) against equity at the start of the forex day
/// (rollover at 22:00 UTC, an approximation of 5pm New York), so open losses count.
/// State is persisted so a restart cannot reset the day's baseline or clear a lockout.
/// </summary>
public sealed class DailyRiskGuard
{
    private static readonly TimeSpan SessionRollover = TimeSpan.FromHours(22);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string? _statePath;
    private readonly GuardState _state;

    public DailyRiskGuard(string? statePath = null)
    {
        _statePath = statePath;
        _state = Load(statePath);
    }

    public static DateTime SessionStart(DateTime nowUtc)
    {
        var shifted = nowUtc - SessionRollover;
        return DateTime.SpecifyKind(shifted.Date, DateTimeKind.Utc) + SessionRollover;
    }

    public GuardStatus Evaluate(decimal equity, DateTime nowUtc)
    {
        lock (_gate)
        {
            var changed = false;
            var session = SessionStart(nowUtc);

            if (_state.StartEquity <= 0 || _state.SessionStartUtc != session)
            {
                _state.SessionStartUtc = session;
                _state.StartEquity = equity;
                changed = true;
            }

            if (_state.LockedUntilUtc is { } until && until <= nowUtc)
            {
                _state.LockedUntilUtc = null;
                _state.LockReason = null;
                changed = true;
            }

            var pnl = equity - _state.StartEquity;
            var newlyLocked = false;

            if (_state.LockedUntilUtc is null)
            {
                var drawdownLimit = _state.StartEquity * RiskLimits.DailyDrawdownPct / 100m;
                var profitTarget = _state.StartEquity * RiskLimits.DailyProfitTargetPct / 100m;

                if (pnl <= -drawdownLimit)
                {
                    _state.LockedUntilUtc = nowUtc + RiskLimits.DrawdownLockout;
                    _state.LockReason =
                        $"Daily drawdown cap hit (P&L {F(pnl)} USD, limit -{F(drawdownLimit)}). Trading paused for 24h.";
                    newlyLocked = true;
                }
                else if (pnl >= profitTarget)
                {
                    _state.LockedUntilUtc = session + TimeSpan.FromHours(24);
                    _state.LockReason =
                        $"Daily profit target reached (P&L +{F(pnl)} USD, target +{F(profitTarget)}). Trading stopped until the next session.";
                    newlyLocked = true;
                }

                changed |= newlyLocked;
            }

            if (changed) Save();

            return new GuardStatus(_state.LockedUntilUtc is null, _state.LockReason, pnl, newlyLocked);
        }
    }

    private static string F(decimal v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private static GuardState Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new GuardState();
        try
        {
            return JsonSerializer.Deserialize<GuardState>(File.ReadAllText(path)) ?? new GuardState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new GuardState();
        }
    }

    private void Save()
    {
        if (_statePath is null) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_statePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions));
        File.Move(tmp, _statePath, overwrite: true);
    }
}
