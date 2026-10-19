using ForexHitl;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForexHitl.Tests;

internal sealed class FakeBroker : IBrokerClient
{
    public decimal Nav = 10_000m;
    public decimal Bid = 1.08500m;
    public decimal Ask = 1.08504m;
    public string Currency = "USD";
    public decimal AudUsdBid = 0.65000m;
    public decimal AudUsdAsk = 0.65004m;
    public bool Tradeable = true;
    public bool OpenPosition;
    public bool ThrowOnOrder;
    public readonly List<TradeProposal> Orders = new();
    public int CandleCalls;

    public Task<AccountSummary> GetAccountAsync(CancellationToken ct) =>
        Task.FromResult(new AccountSummary(Nav, Nav, Currency));

    public Task<Quote> GetQuoteAsync(string instrument, CancellationToken ct) =>
        Task.FromResult(instrument == "AUD_USD"
            ? new Quote(instrument, AudUsdBid, AudUsdAsk, DateTime.UtcNow, Tradeable)
            : new Quote(instrument, Bid, Ask, DateTime.UtcNow, Tradeable));

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string instrument, string granularity, int count, CancellationToken ct)
    {
        CandleCalls++;
        return Task.FromResult<IReadOnlyList<Candle>>(new List<Candle>());
    }

    public Task<bool> HasOpenPositionAsync(string instrument, CancellationToken ct) =>
        Task.FromResult(OpenPosition);

    public Task<OrderResult> PlaceMarketOrderAsync(TradeProposal proposal, CancellationToken ct)
    {
        if (ThrowOnOrder) throw new HttpRequestException("connection reset");
        Orders.Add(proposal);
        return Task.FromResult(new OrderResult(true, $"Filled @ {proposal.EntryPrice}", $"T{Orders.Count}"));
    }
}

internal sealed class FakeCalendar : INewsCalendar
{
    public List<NewsEvent>? Events = new();

    public Task<IReadOnlyList<NewsEvent>?> GetEventsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NewsEvent>?>(Events);
}

internal sealed class RecordingNotifier : IApprovalNotifier
{
    public readonly List<string> Titles = new();

    public Task NotifyAsync(string title, string message, bool urgent, CancellationToken ct)
    {
        Titles.Add(title);
        return Task.CompletedTask;
    }
}

internal sealed class Harness
{
    public readonly FakeClock Clock = new();
    public readonly FakeBroker Broker = new();
    public readonly FakeCalendar Calendar = new();
    public readonly PendingTradeStore Store = new();
    public readonly ActivityLog Activity;
    public readonly RecordingNotifier Notifier = new();
    public readonly ExecutionAgent Agent;
    public readonly DailyRiskGuard Guard;
    public readonly GuardPersistence? Persistence;

    // 1.5 x 6 pip ATR = a 9 pip stop.
    public static readonly Signal BuySignal = new(Side.Buy, 0.0006m, "test signal");

    /// <param name="window">Trading hours; null = no restriction.</param>
    /// <param name="stateStore">When given, the guard loads/saves through it exactly as in production.</param>
    public Harness(TradingWindow? window = null, IStateStore? stateStore = null)
    {
        Activity = new ActivityLog(Clock);
        var gatekeeper = new GatekeeperAgent(Broker, Calendar, Clock);
        Guard = new DailyRiskGuard(awaitingRestore: stateStore is not null);
        if (stateStore is not null)
            Persistence = new GuardPersistence(
                Guard, stateStore, Activity, Clock, NullLogger<GuardPersistence>.Instance);

        Agent = new ExecutionAgent(
            Broker, gatekeeper, Guard, Store, Activity, Notifier, Clock,
            NullLogger<ExecutionAgent>.Instance, Persistence, window);
    }

    public Task Propose() => Agent.ProposeAsync("EUR_USD", BuySignal, CancellationToken.None);

    public async Task<TradeProposal> ProposeOne()
    {
        await Propose();
        return Store.Snapshot().Single();
    }

    public Task<DecisionResult> Decide(string action, string id) =>
        Agent.HandleDecisionAsync(action, id, CancellationToken.None);

    public bool ActivityMentions(string text) => Activity.Snapshot().Any(a => a.Text.Contains(text));
}

public class ExecutionAgentProposeTests
{
    [Fact]
    public async Task Signal_BecomesOneQueuedProposal_AndOnePush()
    {
        var h = new Harness();
        var p = await h.ProposeOne();

        Assert.Equal("EUR_USD", p.Instrument);
        Assert.Equal(Side.Buy, p.Side);
        Assert.True(p.RiskUsd <= 50m);
        Assert.Equal(1, h.Notifier.Titles.Count);
        Assert.Equal("Trade needs approval", h.Notifier.Titles[0]);
        Assert.Equal(0, h.Broker.Orders.Count);   // nothing is traded until a human approves
    }

    [Fact]
    public async Task SecondSignalForSameInstrument_DoesNotDuplicate()
    {
        var h = new Harness();
        await h.Propose();
        await h.Propose();
        Assert.Equal(1, h.Store.Snapshot().Count);
    }

    [Fact]
    public async Task WideSpread_QueuesNothing_AndSaysWhy()
    {
        var h = new Harness();
        h.Broker.Ask = h.Broker.Bid + 0.0001m;    // 1.0 pip > 0.5 pip cap
        await h.Propose();

        Assert.Equal(0, h.Store.Snapshot().Count);
        Assert.True(h.ActivityMentions("Spread"));
    }

    [Fact]
    public async Task OpenPosition_QueuesNothing()
    {
        var h = new Harness();
        h.Broker.OpenPosition = true;
        await h.Propose();
        Assert.Equal(0, h.Store.Snapshot().Count);
    }

    [Fact]
    public async Task NewsBlackout_QueuesNothing()
    {
        var h = new Harness();
        h.Calendar.Events = new() { new NewsEvent(h.Clock.GetUtcNow().UtcDateTime.AddMinutes(20), "USD", "NFP", "High") };
        await h.Propose();

        Assert.Equal(0, h.Store.Snapshot().Count);
        Assert.True(h.ActivityMentions("News blackout"));
    }

    [Fact]
    public async Task CalendarDown_FailsClosed()
    {
        var h = new Harness();
        h.Calendar.Events = null;
        await h.Propose();

        Assert.Equal(0, h.Store.Snapshot().Count);
        Assert.True(h.ActivityMentions("calendar unavailable"));
    }

    [Fact]
    public async Task DrawdownLock_QueuesNothing()
    {
        var h = new Harness();
        await h.Agent.RefreshGuardAsync(CancellationToken.None);   // baseline = $10,000
        h.Broker.Nav = 9_840m;                                     // -$160, past the 1.5% cap
        await h.Propose();

        Assert.Equal(0, h.Store.Snapshot().Count);
    }
}

public class ExecutionAgentApproveTests
{
    [Fact]
    public async Task Approve_PlacesExactlyOneOrder_WithinRisk_AtOneToTwo()
    {
        var h = new Harness();
        var p = await h.ProposeOne();

        var result = await h.Decide("approve", p.Id);

        Assert.True(result.Executed);
        Assert.Equal(1, h.Broker.Orders.Count);
        var o = h.Broker.Orders[0];
        Assert.Equal(Side.Buy, o.Side);
        Assert.Equal(h.Broker.Ask, o.EntryPrice);
        Assert.True(RiskManager.RiskUsd("EUR_USD", o.Units, o.StopLossPips) <= 50m);
        Assert.Equal(2m, (o.TakeProfit - o.EntryPrice) / (o.EntryPrice - o.StopLoss));
        Assert.Equal(0, h.Store.Snapshot().Count);
        Assert.True(h.ActivityMentions("Filled"));
    }

    [Fact]
    public async Task Reject_PlacesNothing()
    {
        var h = new Harness();
        var p = await h.ProposeOne();

        var result = await h.Decide("reject", p.Id);

        Assert.False(result.Executed);
        Assert.Equal(0, h.Broker.Orders.Count);
        Assert.Equal(0, h.Store.Snapshot().Count);
    }

    [Fact]
    public async Task ApproveTwice_PlacesOnlyOneOrder()
    {
        var h = new Harness();
        var p = await h.ProposeOne();

        var first = await h.Decide("approve", p.Id);
        var second = await h.Decide("approve", p.Id);

        Assert.True(first.Executed);
        Assert.False(second.Executed);
        Assert.Equal(1, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task SimultaneousApprovals_PlaceOnlyOneOrder()
    {
        var h = new Harness();
        var p = await h.ProposeOne();

        var results = await Task.WhenAll(h.Decide("approve", p.Id), h.Decide("approve", p.Id));

        Assert.Equal(1, h.Broker.Orders.Count);
        Assert.Equal(1, results.Count(r => r.Executed));
    }

    [Fact]
    public async Task UnknownProposalId_IsHarmless()
    {
        var h = new Harness();
        var result = await h.Decide("approve", "does-not-exist");
        Assert.False(result.Executed);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task Approve_AfterFiveMinutes_IsRefused()
    {
        var h = new Harness();
        var p = await h.ProposeOne();
        h.Clock.Advance(ExecutionAgent.ProposalTtl + TimeSpan.FromSeconds(1));

        var result = await h.Decide("approve", p.Id);

        Assert.False(result.Executed);
        Assert.Contains("expired", result.Message);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task Approve_AfterPriceDrift_IsRefused()
    {
        var h = new Harness();
        var p = await h.ProposeOne();
        h.Broker.Bid += 0.0003m;     // 3 pips against us, over the 2 pip limit
        h.Broker.Ask += 0.0003m;

        var result = await h.Decide("approve", p.Id);

        Assert.False(result.Executed);
        Assert.Contains("Price moved", result.Message);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task Approve_AfterDrawdownCapHit_IsRefused()
    {
        var h = new Harness();
        var p = await h.ProposeOne();      // guard baseline = $10,000
        h.Broker.Nav = 9_840m;

        var result = await h.Decide("approve", p.Id);

        Assert.False(result.Executed);
        Assert.Contains("Trading locked", result.Message);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task Approve_AfterSmallEquityDrop_IsDownsized_NeverUpsized()
    {
        var h = new Harness();
        var p = await h.ProposeOne();      // sized for ~$50 risk on $10,000
        h.Broker.Nav = 9_900m;             // -$100: inside the daily cap, but 0.5% is now $49.50

        var result = await h.Decide("approve", p.Id);

        Assert.True(result.Executed);
        var o = h.Broker.Orders.Single();
        Assert.True(o.Units < p.Units);
        Assert.True(RiskManager.RiskUsd("EUR_USD", o.Units, o.StopLossPips) <= 49.5m);
    }

    [Fact]
    public async Task Approve_IntoNewsBlackout_IsRefused()
    {
        var h = new Harness();
        var p = await h.ProposeOne();
        h.Calendar.Events = new() { new NewsEvent(h.Clock.GetUtcNow().UtcDateTime.AddMinutes(10), "USD", "CPI", "High") };

        var result = await h.Decide("approve", p.Id);

        Assert.False(result.Executed);
        Assert.Contains("News blackout", result.Message);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task Approve_WhenPositionAppearedMeanwhile_IsRefused()
    {
        var h = new Harness();
        var p = await h.ProposeOne();
        h.Broker.OpenPosition = true;

        var result = await h.Decide("approve", p.Id);

        Assert.False(result.Executed);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task OrderRequestFailsInFlight_ReportsUnknownOutcome_AndIsNeverRetried()
    {
        var h = new Harness();
        var p = await h.ProposeOne();
        h.Broker.ThrowOnOrder = true;

        var result = await h.Decide("approve", p.Id);

        Assert.False(result.Executed);
        Assert.Contains("UNKNOWN", result.Message);
        Assert.True(h.ActivityMentions("UNKNOWN"));

        var again = await h.Decide("approve", p.Id);   // proposal is consumed: a second tap cannot resend
        Assert.False(again.Executed);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public void ExpireStale_RemovesOldProposalsAndLogsIt()
    {
        var h = new Harness();
        h.Store.Add(new TradeProposal("old1", "EUR_USD", Side.Buy, 1.1m, 1.099m, 1.102m, 10m, 50_000, 50m,
            h.Clock.GetUtcNow().UtcDateTime.AddMinutes(-6), "t"));

        h.Agent.ExpireStale();

        Assert.Equal(0, h.Store.Snapshot().Count);
        Assert.True(h.ActivityMentions("expired"));
    }
}
