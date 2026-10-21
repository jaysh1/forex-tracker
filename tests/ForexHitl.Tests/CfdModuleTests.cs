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
    public void EngineRiskPolicyCapsRiskAndMarginWithoutForexTypes()
    {
        var contract = new CfdContract("AU200_AUD", 1, 0, 0.1m, 1m, 0.05m, "AUD");
        var policy = new CfdRiskPolicy(50m);

        var units = policy.Size(contract, equity: 10_000m, stopDistance: 100m, entry: 8_000m, quoteToAccount: 1m);

        Assert.Equal(5, units);
    }

    [Fact]
    public void CfdUnitsStayWithinRiskBudgetAndMarginCap()
    {
        var contract = new CfdContract("AU200_AUD", 1, 0, 0.1m, 1m, 0.05m, "AUD");
        var units = CfdRiskSizer.CalculateUnits(contract, equity: 10_000m, stopLossPips: 100m,
            entryPrice: 8_000m, quoteToAccount: 1m, maxRiskPerTrade: 50m);

        Assert.Equal(5, units);
        Assert.Equal(50m, CfdRiskSizer.MaxLoss(contract, units, 100m, 1m));
    }

    [Fact]
    public void CfdSizingAlsoHonorsAccountMarginRate()
    {
        var contract = new CfdContract("SPX500_USD", 1, 0, 1m, 1m, 0.2m, "USD");
        var units = CfdRiskSizer.CalculateUnits(contract, equity: 100_000m, stopLossPips: 0.1m,
            entryPrice: 10_000m, quoteToAccount: 1m, maxRiskPerTrade: 500m);

        Assert.Equal(50, units); // 20% margin means at most 5:1 exposure.
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

        Assert.Throws<InvalidOperationException>(() => CfdSettings.FromEnvironment(true, k => env.GetValueOrDefault(k)));
    }

    [Fact]
    public void CfdInstrumentsAreOptInAndValidatedSeparatelyFromForex()
    {
        var env = new Dictionary<string, string?>
        {
            ["OANDA_API_TOKEN"] = "token", ["OANDA_ACCOUNT_ID"] = "101-001-1-001",
            ["APPROVER_PASSWORD"] = "a-long-enough-password", ["CFD_INSTRUMENTS"] = "AU200_AUD,SPX500_USD"
        };
        var forexConfig = AppConfig.FromEnvironment(k => env.GetValueOrDefault(k));
        var settings = CfdSettings.FromEnvironment(false, k => env.GetValueOrDefault(k));

        Assert.Equal(new[] { "AU200_AUD", "SPX500_USD" }, settings.Instruments);
        Assert.Equal(new[] { "EUR_USD", "GBP_USD" }, forexConfig.Instruments);
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
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"instruments":[{"name":"AU200_AUD","type":"CFD","pipLocation":-1,"displayPrecision":1,"tradeUnitsPrecision":0,"minimumTradeSize":"1","maximumOrderUnits":"250","marginRate":"0.05"}]}""")));
        var client = new CfdOandaClient(new HttpClient(handler) { BaseAddress = new Uri("https://api-fxpractice.oanda.com/") }, "101-001-1-001");

        var contract = await client.GetContractAsync("AU200_AUD", CancellationToken.None);

        Assert.Equal(0.1m, contract.PipSize);
        Assert.Equal(1, contract.PricePrecision);
        Assert.Equal(0.05m, contract.MarginRate);
        Assert.Equal(250m, contract.MaximumUnits);
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
