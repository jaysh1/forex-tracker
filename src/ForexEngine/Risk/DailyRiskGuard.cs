using System.Globalization;

namespace ForexHitl;

public sealed record GuardStatus(bool CanTrade, string? Reason, decimal DayPnl, bool NewlyLocked);

public sealed class GuardState
{
    public DateTime SessionStartUtc { get; set; }
    public decimal StartEquity { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LockReason { get; set; }

    public GuardState Copy() => (GuardState)MemberwiseClone();
}

/// <summary>
/// Enforces the daily drawdown cap (1.5% -> 24h lockout) and daily profit cap (1.0% -> stop until the
/// next session). P&amp;L is measured on equity (NAV) against equity at the start of the forex day
/// (rollover at 22:00 UTC, an approximation of 5pm New York), so open losses count.
///
/// The guard itself does no I/O. <see cref="GuardPersistence"/> loads its state at startup and saves it
/// whenever it changes, so a restart cannot reset the day's baseline or clear a lockout. A guard created
/// with <c>awaitingRestore: true</c> refuses to allow trading until <see cref="Restore"/> has been called.
/// </summary>
public sealed class DailyRiskGuard
{
    public const string AwaitingRestoreReason =
        "Waiting for the saved risk state to load - trading stays paused until it does.";

    private static readonly TimeSpan SessionRollover = TimeSpan.FromHours(22);

    private readonly object _gate = new();
    private GuardState _state = new();
    private bool _ready;
    private bool _dirty;
    private readonly decimal _riskCapital;

    /// <param name="maxRiskPerTrade">The per-trade cap in account currency. Daily limits are calculated against
    /// at most the capital that cap implies at the 0.5% per-trade rate. With the default $50 cap, that is $10,000
    /// of risk capital even if the account balance is much larger.</param>
    public DailyRiskGuard(bool awaitingRestore = false,
        decimal maxRiskPerTrade = RiskManager.DefaultMaxRiskPerTrade)
    {
        if (maxRiskPerTrade <= 0)
            throw new ArgumentException("Maximum risk must be greater than zero.", nameof(maxRiskPerTrade));

        _ready = !awaitingRestore;
        _riskCapital = maxRiskPerTrade / (RiskLimits.RiskPerTradePct / 100m);
    }

    public bool Ready
    {
        get { lock (_gate) return _ready; }
    }

    /// <summary>Installs previously saved state (null = nothing was saved yet) and allows trading decisions.</summary>
    public void Restore(GuardState? saved)
    {
        lock (_gate)
        {
            _state = saved?.Copy() ?? new GuardState();
            _ready = true;
            _dirty = false;
        }
    }

    /// <summary>The state to save if it changed since the last save; null if there is nothing new.</summary>
    public GuardState? TakeDirtySnapshot()
    {
        lock (_gate)
        {
            if (!_dirty) return null;
            _dirty = false;
            return _state.Copy();
        }
    }

    /// <summary>Called when a save failed, so the next flush tries again.</summary>
    public void MarkDirty()
    {
        lock (_gate) _dirty = true;
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
            if (!_ready) return new GuardStatus(false, AwaitingRestoreReason, 0m, false);

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
                var limitBase = Math.Min(_state.StartEquity, _riskCapital);
                var drawdownLimit = limitBase * RiskLimits.DailyDrawdownPct / 100m;
                var profitTarget = limitBase * RiskLimits.DailyProfitTargetPct / 100m;

                if (pnl <= -drawdownLimit)
                {
                    _state.LockedUntilUtc = nowUtc + RiskLimits.DrawdownLockout;
                    _state.LockReason =
                        $"Daily drawdown cap hit (P&L {F(pnl)}, limit -{F(drawdownLimit)}). Trading paused for 24h.";
                    newlyLocked = true;
                }
                else if (pnl >= profitTarget)
                {
                    _state.LockedUntilUtc = session + TimeSpan.FromHours(24);
                    _state.LockReason =
                        $"Daily profit target reached (P&L +{F(pnl)}, target +{F(profitTarget)}). Trading stopped until the next session.";
                    newlyLocked = true;
                }

                changed |= newlyLocked;
            }

            if (changed) _dirty = true;

            return new GuardStatus(_state.LockedUntilUtc is null, _state.LockReason, pnl, newlyLocked);
        }
    }

    private static string F(decimal v) => v.ToString("F2", CultureInfo.InvariantCulture);
}
