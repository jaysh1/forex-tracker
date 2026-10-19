using System.Globalization;

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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IStateStore _store;
    private GuardState _state = new();
    private bool _loaded;

    public DailyRiskGuard(string? statePath = null) : this(new FileStateStore(statePath)) { }

    public DailyRiskGuard(IStateStore store) => _store = store;

    public static DateTime SessionStart(DateTime nowUtc)
    {
        var shifted = nowUtc - SessionRollover;
        return DateTime.SpecifyKind(shifted.Date, DateTimeKind.Utc) + SessionRollover;
    }

    // Kept for callers using a local file; runtime paths use the asynchronous method below.
    public GuardStatus Evaluate(decimal equity, DateTime nowUtc) =>
        EvaluateAsync(equity, nowUtc, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<GuardStatus> EvaluateAsync(decimal equity, DateTime nowUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (equity <= 0) throw new InvalidOperationException("Account equity must be positive.");
            if (!_loaded)
            {
                var saved = await _store.LoadAsync<GuardState>("guard", ct).ConfigureAwait(false);
                if (saved is not null)
                {
                    if (saved.StartEquity <= 0 || saved.SessionStartUtc == default ||
                        saved.SessionStartUtc.Kind != DateTimeKind.Utc || saved.SessionStartUtc != SessionStart(saved.SessionStartUtc) ||
                        saved.SessionStartUtc > SessionStart(nowUtc) ||
                        (saved.LockedUntilUtc is { } untilUtc && (untilUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(saved.LockReason))))
                        throw new InvalidDataException("Invalid saved risk state. Trading is paused; inspect the stored state.");
                    _state = saved;
                }
                _loaded = true;
            }
            var session = SessionStart(nowUtc);

            if (_state.StartEquity <= 0 || _state.SessionStartUtc != session)
            {
                _state.SessionStartUtc = session;
                _state.StartEquity = equity;
            }

            if (_state.LockedUntilUtc is { } until && until <= nowUtc)
            {
                _state.LockedUntilUtc = null;
                _state.LockReason = null;
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

            }

            // Confirm durable storage on every evaluation, including immediately before an approval.
            // If saving fails, retain the exact in-memory baseline/lock and retry it next time.
            await _store.SaveAsync("guard", _state, ct).ConfigureAwait(false);

            return new GuardStatus(_state.LockedUntilUtc is null, _state.LockReason, pnl, newlyLocked);
        }
        finally { _gate.Release(); }
    }

    private static string F(decimal v) => v.ToString("F2", CultureInfo.InvariantCulture);

}
