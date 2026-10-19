using System.Net;
using System.Text;
using System.Text.Json;
using ForexHitl;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForexHitl.Tests;

internal sealed class MemoryStateStore : IStateStore
{
    public Dictionary<string, string> Data = new();
    public bool FailLoad, FailSave;
    public Action? OnSave;
    public Task<T?> LoadAsync<T>(string key, CancellationToken ct) where T : class
    {
        if (FailLoad) throw new HttpRequestException("state unavailable");
        return Task.FromResult(Data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : null);
    }
    public Task SaveAsync<T>(string key, T value, CancellationToken ct) where T : class
    {
        if (FailSave) throw new HttpRequestException("save unavailable");
        Data[key] = JsonSerializer.Serialize(value);
        OnSave?.Invoke();
        return Task.CompletedTask;
    }
}

public class PersistenceTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Restart_RestoresBaselineAndLockout()
    {
        var store = new MemoryStateStore();
        var first = new DailyRiskGuard(store);
        await first.EvaluateAsync(10_000m, Now, default);
        var resumed = new DailyRiskGuard(store);
        var hit = await resumed.EvaluateAsync(9_800m, Now.AddMinutes(1), default);
        Assert.False(hit.CanTrade);
        Assert.Equal(-200m, hit.DayPnl);
        var locked = await new DailyRiskGuard(store).EvaluateAsync(10_000m, Now.AddHours(1), default);
        Assert.False(locked.CanTrade); // recovering equity cannot clear the stored lockout
    }

    [Fact]
    public async Task FailedInitialSave_RetriesOriginalBaseline()
    {
        var store = new MemoryStateStore { FailSave = true };
        var guard = new DailyRiskGuard(store);
        await Assert.ThrowsAsync<HttpRequestException>(() => guard.EvaluateAsync(10_000m, Now, default));
        store.FailSave = false;
        var status = await guard.EvaluateAsync(9_800m, Now.AddMinutes(1), default);
        Assert.False(status.CanTrade);
        Assert.Equal(-200m, status.DayPnl);
        Assert.False((await new DailyRiskGuard(store).EvaluateAsync(10_000m, Now.AddMinutes(2), default)).CanTrade);
    }

    [Fact]
    public async Task FailedLoad_DoesNotCreateFreshBaseline_AndRecoversWhenAvailable()
    {
        var store = new MemoryStateStore();
        await new DailyRiskGuard(store).EvaluateAsync(10_000m, Now, default);
        store.FailLoad = true;
        var resumed = new DailyRiskGuard(store);
        await Assert.ThrowsAsync<HttpRequestException>(() => resumed.EvaluateAsync(9_800m, Now.AddMinutes(1), default));
        store.FailLoad = false;
        Assert.False((await resumed.EvaluateAsync(9_800m, Now.AddMinutes(2), default)).CanTrade);
    }

    [Fact]
    public async Task SupabaseOutageAtApproval_NeverPlacesOrder()
    {
        var store = new MemoryStateStore();
        var h = new Harness(state: store);
        var proposal = await h.ProposeOne();
        store.FailSave = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Decide("approve", proposal.Id));
        Assert.Empty(h.Broker.Orders);
        Assert.Empty(h.Store.Snapshot()); // cannot replay a consumed proposal
        Assert.NotNull(h.Agent.RiskStateError);
        store.FailSave = false;
        await h.Agent.RefreshGuardAsync(default);
        Assert.Null(h.Agent.RiskStateError);
    }

    [Fact]
    public async Task InvalidStoredRiskState_IsNeverSilentlyReset()
    {
        var store = new MemoryStateStore();
        store.Data["guard"] = "{}";
        await Assert.ThrowsAsync<InvalidDataException>(() => new DailyRiskGuard(store).EvaluateAsync(10_000m, Now, default));
        Assert.Equal("{}", store.Data["guard"]);
    }

    [Fact]
    public async Task Activity_RestoresAfterRestart_AndMergesNewEvents()
    {
        var store = new MemoryStateStore();
        var clock = new FakeClock();
        var activity = new ActivityLog(clock);
        activity.Add("test", "before restart");
        await new ActivityPersistence(store, activity, NullLogger<ActivityPersistence>.Instance).SyncAsync(default);
        var resumed = new ActivityLog(clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        resumed.Add("test", "during restore");
        await new ActivityPersistence(store, resumed, NullLogger<ActivityPersistence>.Instance).SyncAsync(default);
        Assert.Equal(new[] { "during restore", "before restart" }, resumed.Snapshot().Select(a => a.Text));
    }
}

internal sealed class StateHttpHandler : HttpMessageHandler
{
    public readonly Dictionary<string, JsonElement> Rows = new();
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string? ApiKey, Authorization;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ApiKey = request.Headers.GetValues("apikey").Single();
        Authorization = request.Headers.Authorization?.ToString();
        if (Status != HttpStatusCode.OK) return new(Status) { Content = new StringContent("private provider details") };
        if (request.Method == HttpMethod.Post)
        {
            Assert.Contains("resolution=merge-duplicates", request.Headers.GetValues("Prefer").Single());
            Assert.Equal("?on_conflict=id", request.RequestUri!.Query);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Rows[doc.RootElement.GetProperty("id").GetString()!] = doc.RootElement.GetProperty("payload").Clone();
            return new(HttpStatusCode.NoContent);
        }
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        var id = query.Split("id=eq.")[1].Split('&')[0];
        var json = Rows.TryGetValue(id, out var payload)
            ? JsonSerializer.Serialize(new[] { new { id, payload } }) : "[]";
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

public class SupabaseStateStoreTests
{
    internal static AppConfig Config(Dictionary<string, string>? extra = null)
    {
        var env = new Dictionary<string, string>
        {
            ["OANDA_API_TOKEN"] = "test", ["OANDA_ACCOUNT_ID"] = "101-001-1-001",
            ["APPROVER_PASSWORD"] = "long-test-password", ["SUPABASE_URL"] = "https://example.test",
            ["SUPABASE_SECRET_KEY"] = "sb_secret_test"
        };
        if (extra is not null) foreach (var (key, value) in extra) env[key] = value;
        return AppConfig.FromEnvironment(key => env.GetValueOrDefault(key));
    }

    [Fact]
    public async Task HttpRoundTrip_RestoresGuardAndSeparatesAccountAndEnvironment()
    {
        var handler = new StateHttpHandler();
        SupabaseStateStore Store(AppConfig cfg) => new(new HttpClient(handler), cfg);
        var store = Store(Config());
        Assert.Null(await store.LoadAsync<GuardState>("guard", default));
        var now = DateTimeOffset.Parse("2026-10-19T00:00:00Z").UtcDateTime;
        await new DailyRiskGuard(store).EvaluateAsync(10_000m, now, default);
        var resumed = new DailyRiskGuard(Store(Config()));
        Assert.False((await resumed.EvaluateAsync(9_800m, now.AddMinutes(1), default)).CanTrade);
        Assert.Equal("sb_secret_test", handler.ApiKey);
        Assert.Null(handler.Authorization);
        Assert.Null(await Store(Config(new() { ["OANDA_ACCOUNT_ID"] = "different" })).LoadAsync<GuardState>("guard", default));
        Assert.Null(await Store(Config(new() { ["OANDA_ENVIRONMENT"] = "live", ["ALLOW_LIVE_TRADING"] = "true" })).LoadAsync<GuardState>("guard", default));
    }

    [Fact]
    public async Task LegacyServiceRole_UsesBearer_AndErrorsDoNotLeakProviderBody()
    {
        var jwt = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"role\":\"service_role\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
        var handler = new StateHttpHandler { Status = HttpStatusCode.Unauthorized };
        var store = new SupabaseStateStore(new HttpClient(handler), Config(new() { ["SUPABASE_SECRET_KEY"] = jwt }));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => store.LoadAsync<GuardState>("guard", default));
        Assert.Equal("Bearer " + jwt, handler.Authorization);
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("private provider details", ex.Message);
        Assert.DoesNotContain(jwt, ex.Message);
    }

    [Theory]
    [InlineData("sb_publishable_test")]
    [InlineData("not-a-key")]
    [InlineData("sb_secret_has spaces")]
    public void PublicOrMalformedKeys_AreRejected(string key) =>
        Assert.Throws<InvalidOperationException>(() => Config(new() { ["SUPABASE_SECRET_KEY"] = key }));

    [Fact]
    public void PartialSupabaseConfig_IsRejected() =>
        Assert.Throws<InvalidOperationException>(() => Config(new() { ["SUPABASE_URL"] = "" }));
}
