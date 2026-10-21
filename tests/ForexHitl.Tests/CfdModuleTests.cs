using ForexHitl;
using CFDEngine;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ForexHitl.Tests;

public sealed class CfdRiskSizerTests
{
    [Fact]
    public void CfdUnitsStayWithinRiskBudgetAndMarginCap()
    {
        var contract = new CfdContract("AU200_AUD", 1, 0, 0.1m, 1m, 0.05m, "AUD");
        var units = CfdRiskSizer.CalculateUnits(contract, equity: 10_000m, stopDistancePoints: 10m, quoteCurrencyPerPointPerUnit: 1m,
            entryPrice: 8_000m, quoteToAccount: 1m, maxRiskPerTrade: 50m);

        Assert.Equal(5, units);
        Assert.Equal(50m, CfdRiskSizer.MaxLoss(units, 10m, 1m, 1m));
    }

    [Fact]
    public void CfdSizingAlsoHonorsAccountMarginRate()
    {
        var contract = new CfdContract("SPX500_USD", 1, 0, 1m, 1m, 0.2m, "USD");
        var units = CfdRiskSizer.CalculateUnits(contract, equity: 100_000m, stopDistancePoints: 0.1m, quoteCurrencyPerPointPerUnit: 1m,
            entryPrice: 10_000m, quoteToAccount: 1m, maxRiskPerTrade: 500m);

        Assert.Equal(50, units); // 20% margin means at most 5:1 exposure.
    }

    [Fact]
    public void CfdMarginSizingUsesTheConfiguredPointValue()
    {
        var contract = new CfdContract("SPX500_USD", 1, 0, 1m, 1m, 0.2m, "USD");

        var units = CfdRiskSizer.CalculateUnits(contract, equity: 100_000m, stopDistancePoints: 0.1m,
            quoteCurrencyPerPointPerUnit: 2m, entryPrice: 10_000m, quoteToAccount: 1m, maxRiskPerTrade: 500m);

        Assert.Equal(25, units);
    }

    [Fact]
    public void LiveAccountsCannotEnableTheCfdPracticeModule()
    {
        var env = new Dictionary<string, string?>
        {
            ["OANDA_API_TOKEN"] = "token", ["OANDA_ACCOUNT_ID"] = "101-001-1-001",
            ["APPROVER_PASSWORD"] = "a-long-enough-password", ["OANDA_ENVIRONMENT"] = "live",
            ["ALLOW_LIVE_TRADING"] = "true"
        };
        env["CFD_INSTRUMENTS"] = "AU200_AUD";
        env["CFD_MAX_SPREAD_POINTS_AU200_AUD"] = "2";
        env["CFD_MAX_APPROVAL_DRIFT_POINTS_AU200_AUD"] = "4";
        env["CFD_QUOTE_CURRENCY_PER_POINT_PER_UNIT_AU200_AUD"] = "1";

        Assert.Throws<InvalidOperationException>(() => CfdSettings.FromEnvironment(true, k => env.GetValueOrDefault(k)));
    }

    [Fact]
    public void CfdInstrumentsAreOptInAndValidatedSeparatelyFromForex()
    {
        var env = new Dictionary<string, string?>
        {
            ["OANDA_API_TOKEN"] = "token", ["OANDA_ACCOUNT_ID"] = "101-001-1-001",
            ["APPROVER_PASSWORD"] = "a-long-enough-password", ["CFD_INSTRUMENTS"] = "AU200_AUD,SPX500_USD",
            ["CFD_MAX_SPREAD_POINTS_AU200_AUD"] = "2", ["CFD_MAX_SPREAD_POINTS_SPX500_USD"] = "3",
            ["CFD_MAX_APPROVAL_DRIFT_POINTS_AU200_AUD"] = "4", ["CFD_MAX_APPROVAL_DRIFT_POINTS_SPX500_USD"] = "6",
            ["CFD_QUOTE_CURRENCY_PER_POINT_PER_UNIT_AU200_AUD"] = "1", ["CFD_QUOTE_CURRENCY_PER_POINT_PER_UNIT_SPX500_USD"] = "1"
        };
        var forexConfig = AppConfig.FromEnvironment(k => env.GetValueOrDefault(k));
        var settings = CfdSettings.FromEnvironment(false, k => env.GetValueOrDefault(k));

        Assert.Equal(new[] { "AU200_AUD", "SPX500_USD" }, settings.Instruments);
        Assert.Equal(new[] { "EUR_USD", "GBP_USD" }, forexConfig.Instruments);
    }

    [Fact]
    public void EnablingCfdRequiresExplicitInstrumentSpecificLimits()
    {
        var env = new Dictionary<string, string?>
        {
            ["CFD_INSTRUMENTS"] = "AU200_AUD", ["CFD_MAX_SPREAD_POINTS_AU200_AUD"] = "2",
            ["CFD_MAX_APPROVAL_DRIFT_POINTS_AU200_AUD"] = "4"
        };

        var error = Assert.Throws<InvalidOperationException>(() => CfdSettings.FromEnvironment(false, key => env.GetValueOrDefault(key)));

        Assert.Contains("CFD_QUOTE_CURRENCY_PER_POINT_PER_UNIT_AU200_AUD", error.Message);
    }
}

public sealed class CfdDailyRiskGuardTests
{
    private static readonly DateTime SessionStart = new(2026, 10, 20, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DailyLimitsUseTheCapitalImpliedByTheCfdRiskCap()
    {
        var guard = new CfdDailyRiskGuard(maxRiskPerTrade: 50m, awaitingRestore: false);
        guard.Evaluate(100_000m, SessionStart);

        var status = guard.Evaluate(99_849m, SessionStart.AddMinutes(10));

        Assert.False(status.CanTrade);
        Assert.True(status.NewlyLocked);
        Assert.Contains("limit -150.00", status.Reason);
        Assert.Equal(-151m, status.DayPnl);
    }

    [Fact]
    public void DailyProfitLimitIsAlsoCappedByTheCfdRiskCap()
    {
        var guard = new CfdDailyRiskGuard(maxRiskPerTrade: 50m, awaitingRestore: false);
        guard.Evaluate(100_000m, SessionStart);

        var status = guard.Evaluate(100_101m, SessionStart.AddMinutes(10));

        Assert.False(status.CanTrade);
        Assert.True(status.NewlyLocked);
        Assert.Contains("target +100.00", status.Reason);
        Assert.Equal(101m, status.DayPnl);
    }

    [Fact]
    public async Task PersistenceRestoresStateBeforeAllowingTradingAndRetriesSaveFailures()
    {
        var store = new MemoryCfdRiskStateStore();
        var first = new CfdDailyRiskGuard(awaitingRestore: true);
        var persistence = new CfdRiskPersistence(first, store, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CfdRiskPersistence>.Instance);
        Assert.False(first.Evaluate(10_000m, SessionStart).CanTrade);

        await persistence.EnsureLoadedAsync(CancellationToken.None);
        Assert.True(first.Ready);
        first.Evaluate(10_000m, SessionStart);
        await persistence.FlushAsync(CancellationToken.None);
        Assert.NotNull(store.Json);

        store.ThrowOnSave = true;
        first.Evaluate(9_849m, SessionStart.AddMinutes(1));
        await persistence.FlushAsync(CancellationToken.None);
        store.ThrowOnSave = false;
        await persistence.FlushAsync(CancellationToken.None);

        Assert.Contains("LockedUntilUtc", store.Json);
        var restored = new CfdDailyRiskGuard(awaitingRestore: true);
        var restore = new CfdRiskPersistence(restored, store, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CfdRiskPersistence>.Instance);
        await restore.EnsureLoadedAsync(CancellationToken.None);
        Assert.False(restored.Evaluate(9_849m, SessionStart.AddMinutes(2)).CanTrade);
    }

    [Fact]
    public async Task PersistenceFailuresPublishDashboardDiagnosticMessages()
    {
        var messages = new List<string>();
        var guard = new CfdDailyRiskGuard(awaitingRestore: true);
        var store = new FailingLoadStore();
        var persistence = new CfdRiskPersistence(guard, store, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CfdRiskPersistence>.Instance, messages.Add);

        await persistence.EnsureLoadedAsync(CancellationToken.None);

        Assert.False(guard.Ready);
        Assert.Contains("Cannot load the saved CFD risk state", Assert.Single(messages));

        var saveMessages = new List<string>();
        var readyGuard = new CfdDailyRiskGuard(awaitingRestore: true);
        var failingStore = new MemoryCfdRiskStateStore { ThrowOnSave = true };
        var savePersistence = new CfdRiskPersistence(readyGuard, failingStore, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CfdRiskPersistence>.Instance, saveMessages.Add);
        await savePersistence.EnsureLoadedAsync(CancellationToken.None);
        readyGuard.Evaluate(10_000m, SessionStart);
        await savePersistence.FlushAsync(CancellationToken.None);

        Assert.Contains("Could not save the CFD risk state", Assert.Single(saveMessages));
    }

    private sealed class MemoryCfdRiskStateStore : ICfdRiskStateStore
    {
        public string Name => "test store";
        public string? Json { get; private set; }
        public bool ThrowOnSave { get; set; }
        public Task<string?> LoadAsync(CancellationToken ct) => Task.FromResult(Json);
        public Task SaveAsync(string json, CancellationToken ct)
        {
            if (ThrowOnSave) throw new IOException("simulated save failure");
            Json = json;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingLoadStore : ICfdRiskStateStore
    {
        public string Name => "test Supabase";
        public Task<string?> LoadAsync(CancellationToken ct) => throw new IOException("simulated unavailable store");
        public Task SaveAsync(string json, CancellationToken ct) => Task.CompletedTask;
    }
}

public sealed class CfdRiskStateStoreScopeTests
{
    [Fact]
    public async Task SupabaseStoreAdapterKeepsCfdGuardRowsScopedToEnvironmentAndAccount()
    {
        var backing = new MemoryStateStore();
        var practiceAccountA = new CfdRiskStateStoreAdapter(
            new ScopedStateStore(backing, "practice:account-a:"), "cfd_guard");
        var practiceAccountB = new CfdRiskStateStoreAdapter(
            new ScopedStateStore(backing, "practice:account-b:"), "cfd_guard");

        await practiceAccountA.SaveAsync("{\"StartEquity\":10000}", CancellationToken.None);
        await practiceAccountB.SaveAsync("{\"StartEquity\":20000}", CancellationToken.None);

        Assert.Equal("{\"StartEquity\":10000}", await practiceAccountA.LoadAsync(CancellationToken.None));
        Assert.Equal("{\"StartEquity\":20000}", await practiceAccountB.LoadAsync(CancellationToken.None));
        Assert.Contains("practice:account-a:cfd_guard", backing.Values.Keys);
        Assert.Contains("practice:account-b:cfd_guard", backing.Values.Keys);
    }

    private sealed class MemoryStateStore : IStateStore
    {
        public string Name => "memory";
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Task<string?> LoadAsync(string key, CancellationToken ct) =>
            Task.FromResult(Values.GetValueOrDefault(key));
        public Task SaveAsync(string key, string json, CancellationToken ct)
        {
            Values[key] = json;
            return Task.CompletedTask;
        }
    }
}

public sealed class CfdOandaClientTests
{
    [Fact]
    public async Task ReadsContractPrecisionMarginAndAccountLimitsFromOanda()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"instruments":[{"name":"AU200_AUD","type":"CFD","pipLocation":-1,"displayPrecision":1,"tradeUnitsPrecision":0,"minimumTradeSize":"1","maximumOrderUnits":"250","maximumPositionSize":"200","marginRate":"0.05"}]}""")));
        var client = new CfdOandaClient(new HttpClient(handler) { BaseAddress = new Uri("https://api-fxpractice.oanda.com/") }, "101-001-1-001");

        var contract = await client.GetContractAsync("AU200_AUD", CancellationToken.None);

        Assert.Equal(0.1m, contract.PipSize);
        Assert.Equal(1, contract.PricePrecision);
        Assert.Equal(0.05m, contract.MarginRate);
        Assert.Equal(200m, contract.MaximumUnits);
        Assert.Equal("AUD", contract.QuoteCurrency);
    }

    [Fact]
    public async Task StopAndTargetOrdersUseInstrumentPricePrecision()
    {
        string? posted = null;
        var handler = new StubHandler(async request =>
        {
            posted = await request.Content!.ReadAsStringAsync();
            return Json("""{"orderFillTransaction":{"price":"8000.2","tradeOpened":{"tradeID":"123"}}}""");
        });
        var client = new CfdOandaClient(new HttpClient(handler) { BaseAddress = new Uri("https://api-fxpractice.oanda.com/") }, "101-001-1-001");
        var proposal = new CfdTradeProposal("a1", "AU200_AUD", CfdSide.Buy, 2, 8001.2m, 7990.1m, 8023.4m,
            111m, 22.2m, "AUD", DateTime.UtcNow, "test", 1, 0.1m, 0.05m);

        var result = await client.PlaceMarketOrderAsync(proposal, CancellationToken.None);

        Assert.True(result.Filled);
        using var doc = JsonDocument.Parse(posted!);
        Assert.Equal("7990.1", doc.RootElement.GetProperty("order").GetProperty("stopLossOnFill").GetProperty("price").GetString());
        Assert.Equal("8023.4", doc.RootElement.GetProperty("order").GetProperty("takeProfitOnFill").GetProperty("price").GetString());
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}

public sealed class CfdApprovalTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExpiredProposalIsRejectedBeforeBrokerCalls()
    {
        var fixture = new Fixture();
        fixture.Queue(Now.AddMinutes(-6));

        var result = await fixture.Agent.DecideAsync("approve", "proposal", CancellationToken.None);

        Assert.Equal("Proposal expired.", result.Message);
        Assert.Equal(0, fixture.Broker.AccountCalls);
    }

    [Fact]
    public async Task ApprovalOutsideConfiguredHoursDoesNotReachBroker()
    {
        var fixture = new Fixture(hours: "00:00-01:00");
        fixture.Queue(Now);

        var result = await fixture.Agent.DecideAsync("approve", "proposal", CancellationToken.None);

        Assert.Contains("Outside CFD practice hours", result.Message);
        Assert.Equal(0, fixture.Broker.AccountCalls);
    }

    [Fact]
    public async Task PriceDriftBlocksApproval()
    {
        var fixture = new Fixture();
        fixture.Broker.Quote = new CfdQuote("AU200_AUD", 110m, 111m, Now, true);
        fixture.Queue(Now);

        var result = await fixture.Agent.DecideAsync("approve", "proposal", CancellationToken.None);

        Assert.Contains("Price moved", result.Message);
        Assert.Equal(0, fixture.Broker.OrderCalls);
    }

    [Fact]
    public async Task LockedGuardBlocksApproval()
    {
        var fixture = new Fixture(locked: true);
        fixture.Queue(Now);

        var result = await fixture.Agent.DecideAsync("approve", "proposal", CancellationToken.None);

        Assert.Contains("Trading locked", result.Message);
        Assert.Equal(0, fixture.Broker.OrderCalls);
    }

    [Fact]
    public async Task FailedOandaOrderIsReturnedAsFailedAndNotRetried()
    {
        var fixture = new Fixture();
        fixture.Broker.OrderResult = new CfdOrderResult(false, "Order not filled: MARKET_HALTED", null);
        fixture.Queue(Now);

        var result = await fixture.Agent.DecideAsync("approve", "proposal", CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Contains("MARKET_HALTED", result.Message);
        Assert.Equal(1, fixture.Broker.OrderCalls);
    }

    [Fact]
    public async Task SpreadWideningAndNewsBlackoutBlockApproval()
    {
        var wide = new Fixture();
        wide.Broker.Quote = new CfdQuote("AU200_AUD", 99m, 103m, Now, true);
        wide.Queue(Now);
        Assert.Contains("Spread widened", (await wide.Agent.DecideAsync("approve", "proposal", CancellationToken.None)).Message);
        Assert.Equal(0, wide.Broker.OrderCalls);

            var news = new Fixture(events: new[] { new CfdNewsEvent(Now, "AUD", "Rate decision", "High") });
        news.Queue(Now);
        Assert.Contains("News blackout", (await news.Agent.DecideAsync("approve", "proposal", CancellationToken.None)).Message);
        Assert.Equal(0, news.Broker.OrderCalls);
    }

    private sealed class Fixture
    {
        public FakeBroker Broker { get; } = new();
        public CfdPracticeAgent Agent { get; }

        public Fixture(string hours = "always", bool locked = false, IReadOnlyList<CfdNewsEvent>? events = null)
        {
            var time = new FixedTimeProvider(Now);
            var settings = new CfdSettings(new[] { "AU200_AUD" },
                CfdTradingWindow.Parse(hours, "Mon-Sun", "UTC"), 50m,
                new Dictionary<string, decimal> { ["AU200_AUD"] = 2m },
                new Dictionary<string, decimal> { ["AU200_AUD"] = 4m },
                new Dictionary<string, decimal> { ["AU200_AUD"] = 1m });
            var guard = new CfdDailyRiskGuard(50m, awaitingRestore: true);
            if (locked) guard.Restore(new CfdGuardState(CfdDailyRiskGuard.SessionStart(Now), 10_000m,
                Now.AddHours(1), "test lock"));
            var persistence = new CfdRiskPersistence(guard, new EmptyStore(), time,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CfdRiskPersistence>.Instance);
            Agent = new CfdPracticeAgent(settings, Broker, new FakeNews(events ?? Array.Empty<CfdNewsEvent>()), new CfdTrendPullbackStrategy(),
                guard, persistence, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<CfdPracticeAgent>.Instance);
        }

        public void Queue(DateTime created) => Agent.QueueProposal(new CfdTradeProposal("proposal", "AU200_AUD",
            CfdSide.Buy, 50, 100m, 99m, 102m, 1m, 50m, "AUD", created, "test", 1, 0.1m, 0.05m));
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(now, DateTimeKind.Utc));
    }

    private sealed class EmptyStore : ICfdRiskStateStore
    {
        public string Name => "test";
        public Task<string?> LoadAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SaveAsync(string json, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeNews(IReadOnlyList<CfdNewsEvent>? events) : ICfdNewsCalendar
    {
        public Task<IReadOnlyList<CfdNewsEvent>?> GetEventsAsync(CancellationToken ct) => Task.FromResult(events);
    }

    private sealed class FakeBroker : ICfdBroker
    {
        public CfdQuote Quote { get; set; } = new("AU200_AUD", 99m, 100m, Now, true);
        public CfdOrderResult OrderResult { get; set; } = new(true, "filled", "trade-1");
        public int AccountCalls { get; private set; }
        public int OrderCalls { get; private set; }
        public Task<CfdAccount> GetAccountAsync(CancellationToken ct) { AccountCalls++; return Task.FromResult(new CfdAccount(10_000m, 10_000m, "AUD")); }
        public Task<CfdContract> GetContractAsync(string symbol, CancellationToken ct) => Task.FromResult(new CfdContract(symbol, 1, 0, 0.1m, 1m, 0.05m, "AUD", 1000m));
        public Task<CfdQuote> GetQuoteAsync(string symbol, CancellationToken ct) => Task.FromResult(Quote);
        public Task<IReadOnlyList<CfdCandle>> GetCandlesAsync(string symbol, string interval, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<CfdCandle>>(Array.Empty<CfdCandle>());
        public Task<bool> HasOpenPositionAsync(string symbol, CancellationToken ct) => Task.FromResult(false);
        public Task<decimal> CurrencyToAccountAsync(string currency, string accountCurrency, CancellationToken ct) => Task.FromResult(1m);
        public Task<CfdOrderResult> PlacePracticeOrderAsync(CfdTradeProposal request, CancellationToken ct) { OrderCalls++; return Task.FromResult(OrderResult); }
    }
}
