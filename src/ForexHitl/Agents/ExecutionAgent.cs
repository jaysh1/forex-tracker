using System.Globalization;

namespace ForexHitl;

public sealed record StatusSnapshot(DateTime UpdatedUtc, decimal Equity, decimal DayPnl, bool CanTrade, string? Reason);

/// <summary>
/// Risk &amp; Execution Management: sizes trades, queues them for human approval, and - only after an
/// explicit "approve" from the web UI - re-validates everything and places the order. All propose/execute
/// paths are serialised by a single lock so state checks are race-free.
/// </summary>
public sealed class ExecutionAgent
{
    public static readonly TimeSpan ProposalTtl = TimeSpan.FromMinutes(5);
    public const decimal MaxEntryDriftPips = 2m;

    private readonly IBrokerClient _broker;
    private readonly GatekeeperAgent _gatekeeper;
    private readonly DailyRiskGuard _guard;
    private readonly PendingTradeStore _store;
    private readonly ActivityLog _activity;
    private readonly IApprovalNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<ExecutionAgent> _log;
    private readonly GuardPersistence? _persistence;
    private readonly TradingWindow _window;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile StatusSnapshot? _lastStatus;

    public ExecutionAgent(
        IBrokerClient broker, GatekeeperAgent gatekeeper, DailyRiskGuard guard, PendingTradeStore store,
        ActivityLog activity, IApprovalNotifier notifier, TimeProvider time, ILogger<ExecutionAgent> log,
        GuardPersistence? persistence = null, TradingWindow? tradingWindow = null)
    {
        _broker = broker;
        _gatekeeper = gatekeeper;
        _guard = guard;
        _store = store;
        _activity = activity;
        _notifier = notifier;
        _time = time;
        _log = log;
        _persistence = persistence;
        _window = tradingWindow ?? TradingWindow.Always;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private bool InTradingHours => _window.IsOpen(Now);

    /// <summary>
    /// Every guard check goes through here: make sure the saved state has been loaded (trading stays paused
    /// until it is), evaluate, then save the state if it changed.
    /// </summary>
    private async Task<GuardStatus> EvaluateGuardAsync(decimal equity, DateTime now, CancellationToken ct)
    {
        if (_persistence is not null) await _persistence.EnsureLoadedAsync(ct);
        var status = _guard.Evaluate(equity, now);
        if (_persistence is not null) await _persistence.FlushAsync(ct);
        return status;
    }

    /// <summary>Latest account/guard status for the UI; null until the scanner has completed one tick.</summary>
    public StatusSnapshot? LastStatus => _lastStatus;

    /// <summary>Evaluates the daily caps against live equity and announces a new lockout once.</summary>
    public async Task<GuardStatus> RefreshGuardAsync(CancellationToken ct)
    {
        var acct = await _broker.GetAccountAsync(ct);
        var status = await EvaluateGuardAsync(acct.Nav, Now, ct);
        _lastStatus = new StatusSnapshot(Now, acct.Nav, status.DayPnl, status.CanTrade, status.Reason);

        if (status.NewlyLocked)
        {
            _log.LogWarning("Trading locked: {Reason}", status.Reason);
            _activity.Add("lock", status.Reason ?? "Trading locked");
            await _notifier.NotifyAsync("Trading paused", status.Reason ?? "Trading locked", urgent: true, ct);
        }
        return status;
    }

    public async Task ProposeAsync(string instrument, Signal signal, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!InTradingHours) return;
            if (_store.HasPending(instrument)) return;

            var acct = await _broker.GetAccountAsync(ct);
            if (!(await EvaluateGuardAsync(acct.Nav, Now, ct)).CanTrade) return;
            if (await _broker.HasOpenPositionAsync(instrument, ct)) return;

            var gate = await _gatekeeper.CheckAsync(instrument, ct);
            if (!gate.Allowed)
            {
                _log.LogInformation("{Instrument} signal blocked by gatekeeper: {Reason}", instrument, gate.Reason);
                _activity.Add("blocked", $"{instrument} {signal.Side} signal skipped: {gate.Reason}");
                return;
            }

            var fx = await AccountCurrency.GetUsdToAccountAsync(_broker, acct.Currency, ct);
            var proposal = ProposalBuilder.Build(instrument, signal, gate.Quote!, acct.Nav, Now, fx);
            if (proposal is null)
            {
                _log.LogInformation("{Instrument} signal skipped: cannot size within risk/leverage limits", instrument);
                _activity.Add("blocked", $"{instrument} {signal.Side} signal skipped: cannot size within risk/leverage limits");
                return;
            }

            _store.Add(proposal);
            _activity.Add("proposal",
                $"{instrument} {proposal.Side} {proposal.Units:N0} units, risk ${F(proposal.RiskUsd)} - waiting for your approval");
            _log.LogInformation("Proposal {Id}: {Instrument} {Side} {Units}", proposal.Id, instrument, proposal.Side, proposal.Units);

            await _notifier.NotifyAsync(
                "Trade needs approval",
                $"{instrument} {proposal.Side} {proposal.Units:N0} units, risk ${F(proposal.RiskUsd)}. Expires in {(int)ProposalTtl.TotalMinutes} min.",
                urgent: false, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes proposals that nobody answered within the TTL.</summary>
    public void ExpireStale()
    {
        foreach (var p in _store.TakeExpired(Now - ProposalTtl))
            _activity.Add("expired", $"{p.Instrument} {p.Side} proposal expired - no action taken");
    }

    public async Task<DecisionResult> HandleDecisionAsync(string action, string proposalId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_store.TryTake(proposalId, out var p))
                return new DecisionResult(false, "Already handled or expired.");

            var tag = $"{p.Instrument} {p.Side}";

            if (action == "reject")
            {
                _activity.Add("rejected", $"{tag} rejected by you");
                return new DecisionResult(false, "Rejected.");
            }

            var now = Now;
            if (now - p.CreatedUtc > ProposalTtl) return Refuse(p, "Proposal expired");
            if (!_window.IsOpen(now)) return Refuse(p, $"Outside trading hours ({_window.Describe()})");

            var acct = await _broker.GetAccountAsync(ct);
            var status = await EvaluateGuardAsync(acct.Nav, now, ct);
            if (!status.CanTrade) return Refuse(p, $"Trading locked: {status.Reason}");

            var gate = await _gatekeeper.CheckAsync(p.Instrument, ct);
            if (!gate.Allowed) return Refuse(p, gate.Reason ?? "Gatekeeper blocked");

            var live = p.Side == Side.Buy ? gate.Quote!.Ask : gate.Quote!.Bid;
            var driftPips = Math.Abs(live - p.EntryPrice) / Instruments.PipSize(p.Instrument);
            if (driftPips > MaxEntryDriftPips)
                return Refuse(p, $"Price moved {F(driftPips)} pips since the proposal");

            if (await _broker.HasOpenPositionAsync(p.Instrument, ct))
                return Refuse(p, "A position in this instrument is already open");

            // Equity and the exchange rate move between proposal and approval. Re-size against today's numbers:
            // never larger than what was approved, and refuse if it would have to shrink by more than 5%.
            var fx = await AccountCurrency.GetUsdToAccountAsync(_broker, acct.Currency, ct);
            var maxUnits = RiskManager.CalculateUnits(p.Instrument, acct.Nav, p.StopLossPips, live, fx);
            var units = Math.Min(p.Units, maxUnits);
            if (units < 1 || units < p.Units * 0.95m)
                return Refuse(p, "Equity or the exchange rate has moved since the proposal, so the approved size is no longer within 0.5% risk");

            var order = ProposalBuilder.Reanchor(p, live) with
            {
                Units = units,
                RiskUsd = Math.Round(RiskManager.RiskUsd(p.Instrument, units, p.StopLossPips, fx), 2)
            };
            // The checks above make network calls; the window may have closed while they ran.
            if (!InTradingHours) return Refuse(p, $"Outside trading hours ({_window.Describe()})");

            OrderResult result;
            try
            {
                result = await _broker.PlaceMarketOrderAsync(order, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Order placement for {Id} failed with unknown outcome", p.Id);
                const string unknown = "Order status UNKNOWN - the request failed in flight. Check your OANDA account before doing anything else.";
                _activity.Add("error", $"{tag}: {unknown}");
                return new DecisionResult(false, unknown);
            }

            if (result.Success)
            {
                _activity.Add("executed",
                    $"{tag} {order.Units:N0} units. {result.Message}. SL {F5(order.StopLoss)} TP {F5(order.TakeProfit)} (trade {result.TradeId ?? "n/a"})");
                _log.LogInformation("Proposal {Id} executed: {Message}", p.Id, result.Message);
                return new DecisionResult(true, $"Order placed. {result.Message}");
            }

            _activity.Add("not-filled", $"{tag}: {result.Message}");
            return new DecisionResult(false, result.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DecisionResult Refuse(TradeProposal p, string reason)
    {
        _activity.Add("refused", $"{p.Instrument} {p.Side} not executed: {reason}");
        return new DecisionResult(false, $"Not executed: {reason}");
    }

    private static string F(decimal v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string F5(decimal v) => v.ToString("F5", CultureInfo.InvariantCulture);
}
