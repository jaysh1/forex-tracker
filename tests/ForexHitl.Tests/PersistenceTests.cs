using System.Net;
using System.Text;
using System.Text.Json;
using ForexHitl;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForexHitl.Tests;

internal sealed class FakeStore : IStateStore
{
    public string Name => "fake store";
    public readonly Dictionary<string, string> Data = new();
    public bool FailLoad;
    public bool FailSave;
    public int Saves;

    public Task<string?> LoadAsync(string key, CancellationToken ct)
    {
        if (FailLoad) throw new HttpRequestException("store is down");
        return Task.FromResult<string?>(Data.TryGetValue(key, out var v) ? v : null);
    }

    public Task SaveAsync(string key, string json, CancellationToken ct)
    {
        if (FailSave) throw new HttpRequestException("store is down");
        Data[key] = json;
        Saves++;
        return Task.CompletedTask;
    }
}

public class GuardPersistenceTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);

    private sealed record Rig(DailyRiskGuard Guard, GuardPersistence Persistence, ActivityLog Activity, FakeClock Clock);

    private static Rig Make(FakeStore store, FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        var activity = new ActivityLog(clock);
        var guard = new DailyRiskGuard(awaitingRestore: true);
        var persistence = new GuardPersistence(guard, store, activity, clock, NullLogger<GuardPersistence>.Instance);
        return new Rig(guard, persistence, activity, clock);
    }

    private static bool Mentions(Rig r, string text) => r.Activity.Snapshot().Any(a => a.Text.Contains(text));

    [Fact]
    public async Task NothingSavedYet_StartsFresh_AndSavesTheBaseline()
    {
        var store = new FakeStore();
        var r = Make(store);

        await r.Persistence.EnsureLoadedAsync(CancellationToken.None);
        Assert.True(r.Guard.Ready);
        Assert.True(r.Guard.Evaluate(10_000m, T0).CanTrade);

        await r.Persistence.FlushAsync(CancellationToken.None);
        Assert.True(store.Data.ContainsKey("guard"));
        Assert.Contains("10000", store.Data["guard"]);
    }

    [Fact]
    public async Task LockoutSurvivesARestart()
    {
        var store = new FakeStore();

        var first = Make(store);
        await first.Persistence.EnsureLoadedAsync(CancellationToken.None);
        first.Guard.Evaluate(10_000m, T0);
        Assert.False(first.Guard.Evaluate(9_800m, T0.AddHours(1)).CanTrade);
        await first.Persistence.FlushAsync(CancellationToken.None);

        var afterRestart = Make(store);                       // a brand new process, same store
        await afterRestart.Persistence.EnsureLoadedAsync(CancellationToken.None);
        var status = afterRestart.Guard.Evaluate(9_800m, T0.AddHours(2));

        Assert.False(status.CanTrade);
        Assert.Contains("drawdown", status.Reason!);
    }

    [Fact]
    public async Task TheDaysBaselineSurvivesARestart()
    {
        var store = new FakeStore();

        var first = Make(store);
        await first.Persistence.EnsureLoadedAsync(CancellationToken.None);
        first.Guard.Evaluate(10_000m, T0);
        await first.Persistence.FlushAsync(CancellationToken.None);

        var afterRestart = Make(store);
        await afterRestart.Persistence.EnsureLoadedAsync(CancellationToken.None);
        var status = afterRestart.Guard.Evaluate(9_900m, T0.AddHours(1));

        Assert.Equal(-100m, status.DayPnl);                    // measured against 10,000, not against today's 9,900
    }

    [Fact]
    public async Task LoadFailure_FailsClosed_ThenRecovers()
    {
        var store = new FakeStore { FailLoad = true };
        var r = Make(store);

        await r.Persistence.EnsureLoadedAsync(CancellationToken.None);

        Assert.False(r.Guard.Ready);
        var blocked = r.Guard.Evaluate(10_000m, T0);
        Assert.False(blocked.CanTrade);
        Assert.True(Mentions(r, "Cannot load the saved risk state"));

        store.FailLoad = false;
        await r.Persistence.EnsureLoadedAsync(CancellationToken.None);

        Assert.True(r.Guard.Ready);
        Assert.True(r.Guard.Evaluate(10_000m, T0).CanTrade);
    }

    [Fact]
    public async Task UnreadableSavedState_FailsClosed_InsteadOfResetting()
    {
        foreach (var bad in new[] { "not json {", "null" })
        {
            var store = new FakeStore();
            store.Data["guard"] = bad;
            var r = Make(store);

            await r.Persistence.EnsureLoadedAsync(CancellationToken.None);

            Assert.False(r.Guard.Ready);
            Assert.False(r.Guard.Evaluate(10_000m, T0).CanTrade);
            Assert.True(Mentions(r, "Cannot load the saved risk state"));
        }
    }

    [Fact]
    public async Task SaveFailure_KeepsTheLockInMemory_AndRetries()
    {
        var store = new FakeStore();
        var r = Make(store);
        await r.Persistence.EnsureLoadedAsync(CancellationToken.None);

        store.FailSave = true;
        r.Guard.Evaluate(10_000m, T0);
        Assert.False(r.Guard.Evaluate(9_800m, T0.AddHours(1)).CanTrade);
        await r.Persistence.FlushAsync(CancellationToken.None);

        Assert.False(store.Data.ContainsKey("guard"));
        Assert.True(Mentions(r, "Could not save the risk state"));
        Assert.False(r.Guard.Evaluate(9_800m, T0.AddHours(2)).CanTrade);   // still locked in memory

        store.FailSave = false;
        await r.Persistence.FlushAsync(CancellationToken.None);
        Assert.True(store.Data.ContainsKey("guard"));
        Assert.Contains("Daily drawdown cap", store.Data["guard"]);
    }

    [Fact]
    public async Task UnchangedState_IsNotSavedAgain()
    {
        var store = new FakeStore();
        var r = Make(store);
        await r.Persistence.EnsureLoadedAsync(CancellationToken.None);

        r.Guard.Evaluate(10_000m, T0);
        await r.Persistence.FlushAsync(CancellationToken.None);
        Assert.Equal(1, store.Saves);

        r.Guard.Evaluate(10_020m, T0.AddMinutes(5));
        await r.Persistence.FlushAsync(CancellationToken.None);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task RepeatedSaveFailures_AreReportedOnce_ThenAgainAfterTenMinutes()
    {
        var store = new FakeStore { FailSave = true };
        var r = Make(store);
        await r.Persistence.EnsureLoadedAsync(CancellationToken.None);
        r.Guard.Evaluate(10_000m, T0);

        for (var i = 0; i < 3; i++) await r.Persistence.FlushAsync(CancellationToken.None);
        Assert.Equal(1, r.Activity.Snapshot().Count(a => a.Text.Contains("Could not save")));

        r.Clock.Advance(TimeSpan.FromMinutes(11));
        await r.Persistence.FlushAsync(CancellationToken.None);
        Assert.Equal(2, r.Activity.Snapshot().Count(a => a.Text.Contains("Could not save")));
    }
}

public class PersistenceThroughTheExecutionAgentTests
{
    [Fact]
    public async Task WhenSavedStateCannotBeLoaded_NothingIsProposed_UntilItCan()
    {
        var store = new FakeStore { FailLoad = true };
        var h = new Harness(null, store);

        await h.Propose();
        Assert.Equal(0, h.Store.Snapshot().Count);

        var status = await h.Agent.RefreshGuardAsync(CancellationToken.None);
        Assert.False(status.CanTrade);
        Assert.Contains("saved risk state", status.Reason!);

        store.FailLoad = false;
        await h.Propose();
        Assert.Equal(1, h.Store.Snapshot().Count);
    }

    [Fact]
    public async Task ALockout_IsSavedAsSoonAsItHappens()
    {
        var store = new FakeStore();
        var h = new Harness(null, store);

        await h.Agent.RefreshGuardAsync(CancellationToken.None);            // loads, sets the baseline
        h.Broker.Nav = 9_840m;                                              // -$160: past the 1.5% cap
        var status = await h.Agent.RefreshGuardAsync(CancellationToken.None);

        Assert.False(status.CanTrade);
        Assert.Contains("Daily drawdown cap", store.Data["guard"]);
    }

    [Fact]
    public async Task AfterARestart_ApprovalIsStillRefused_WhileLocked()
    {
        var store = new FakeStore();
        var before = new Harness(null, store);
        await before.Agent.RefreshGuardAsync(CancellationToken.None);
        before.Broker.Nav = 9_840m;
        await before.Agent.RefreshGuardAsync(CancellationToken.None);

        var after = new Harness(null, store);                               // same store, new process
        after.Broker.Nav = 9_840m;
        await after.Propose();

        Assert.Equal(0, after.Store.Snapshot().Count);
    }
}

public class ActivityPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Restore_PutsOldEntriesBehindNewOnes_AndKeepsFifty()
    {
        var clock = new FakeClock();
        var log = new ActivityLog(clock);
        for (var i = 0; i < 3; i++) log.Add("new", $"new {i}");

        var old = Enumerable.Range(0, 60)
            .Select(i => new ActivityEntry(Now.UtcDateTime.AddHours(-1).AddMinutes(-i), "old", $"old {i}"))
            .ToList();
        var versionBefore = log.Version;

        log.Restore(old);

        var items = log.Snapshot();
        Assert.Equal(50, items.Count);
        Assert.Equal("new", items[0].Kind);
        Assert.Equal("old 0", items[3].Text);
        Assert.True(log.Version > versionBefore);
    }

    private static StateSyncService Service(FakeStore store, out ActivityLog activity, out GuardPersistence persistence)
    {
        var clock = new FakeClock();
        activity = new ActivityLog(clock);
        persistence = new GuardPersistence(
            new DailyRiskGuard(awaitingRestore: true), store, activity, clock, NullLogger<GuardPersistence>.Instance);
        return new StateSyncService(persistence, activity, store, NullLogger<StateSyncService>.Instance);
    }

    [Fact]
    public async Task StartLoadsTheFeedAndGuard_StopSavesTheLatestFeed()
    {
        var store = new FakeStore();
        store.Data["activity"] = JsonSerializer.Serialize(new List<ActivityEntry>
        {
            new(Now.UtcDateTime.AddHours(-2), "executed", "from the last run")
        });
        var svc = Service(store, out var activity, out _);

        await svc.StartAsync(CancellationToken.None);
        Assert.True(activity.Snapshot().Any(a => a.Text == "from the last run"));
        Assert.True(activity.Snapshot().Any(a => a.Text.Contains("No saved risk state")));

        activity.Add("proposal", "added while running");
        await svc.StopAsync(CancellationToken.None);

        Assert.Contains("added while running", store.Data["activity"]);
        Assert.Contains("from the last run", store.Data["activity"]);
    }

    [Fact]
    public async Task FeedSaveFailure_IsRetriedLater_AndUnchangedFeedIsNotResaved()
    {
        var store = new FakeStore { FailSave = true };
        var svc = Service(store, out var activity, out _);
        activity.Add("info", "something happened");

        await svc.SaveActivityIfChangedAsync(CancellationToken.None);      // fails quietly
        Assert.False(store.Data.ContainsKey("activity"));

        store.FailSave = false;
        await svc.SaveActivityIfChangedAsync(CancellationToken.None);
        Assert.True(store.Data.ContainsKey("activity"));
        Assert.Equal(1, store.Saves);

        await svc.SaveActivityIfChangedAsync(CancellationToken.None);      // nothing new
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task UnreadableSavedFeed_DoesNotStopStartup()
    {
        var store = new FakeStore();
        store.Data["activity"] = "this is not json";
        var svc = Service(store, out var activity, out var persistence);

        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        Assert.True(activity.Snapshot().Count > 0);                        // the guard still loaded and logged
    }
}

internal sealed class CapturingHandler : HttpMessageHandler
{
    public sealed record Seen(string Method, string Url, string? Prefer, string? ApiKey, string? Authorization, string Body);

    public readonly List<Seen> Requests = new();
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string Response = "[]";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? First(string name) => request.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

        Requests.Add(new Seen(
            request.Method.Method, request.RequestUri!.ToString(), First("Prefer"), First("apikey"),
            request.Headers.Authorization?.ToString(),
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));

        return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
    }
}

public class SupabaseStateStoreTests
{
    private const string Url = "https://abcd.supabase.co";
    private const string SecretKey = "sb_secret_super-secret-value";

    private static SupabaseStateStore Store(CapturingHandler handler, string key = SecretKey)
    {
        var http = new HttpClient(handler);
        SupabaseStateStore.Configure(http, Url, key);
        return new SupabaseStateStore(http);
    }

    [Fact]
    public async Task Load_ReadsTheValueColumnOfTheMatchingRow()
    {
        var handler = new CapturingHandler { Response = """[{"value":{"StartEquity":10000}}]""" };

        var json = await Store(handler).LoadAsync("guard", CancellationToken.None);

        Assert.Equal("""{"StartEquity":10000}""", json);
        var req = handler.Requests.Single();
        Assert.Equal("GET", req.Method);
        Assert.Equal("https://abcd.supabase.co/rest/v1/bot_state?select=value&key=eq.guard", req.Url);
    }

    [Fact]
    public async Task Load_WithNoRow_ReturnsNull()
    {
        var handler = new CapturingHandler { Response = "[]" };
        Assert.Null(await Store(handler).LoadAsync("guard", CancellationToken.None));
    }

    [Fact]
    public async Task Save_UpsertsASingleRow()
    {
        var handler = new CapturingHandler { Status = HttpStatusCode.Created, Response = "" };

        await Store(handler).SaveAsync("guard", """{"StartEquity":10000}""", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal("POST", req.Method);
        Assert.Equal("https://abcd.supabase.co/rest/v1/bot_state", req.Url);
        Assert.Contains("resolution=merge-duplicates", req.Prefer!);

        using var body = JsonDocument.Parse(req.Body);
        Assert.Equal("guard", body.RootElement.GetProperty("key").GetString());
        Assert.Equal(10000m, body.RootElement.GetProperty("value").GetProperty("StartEquity").GetDecimal());
        Assert.True(body.RootElement.TryGetProperty("updated_at", out _));
    }

    [Fact]
    public async Task NewStyleSecretKey_GoesOnTheApikeyHeaderOnly()
    {
        var handler = new CapturingHandler();
        await Store(handler).LoadAsync("guard", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(SecretKey, req.ApiKey);
        Assert.Null(req.Authorization);
    }

    [Fact]
    public async Task LegacyJwtKey_GoesOnBothHeaders()
    {
        const string legacy = "eyJhbGciOiJIUzI1NiJ9.eyJyb2xlIjoic2VydmljZV9yb2xlIn0.signature";
        var handler = new CapturingHandler();
        await Store(handler, legacy).LoadAsync("guard", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(legacy, req.ApiKey);
        Assert.Equal("Bearer " + legacy, req.Authorization);
    }

    [Fact]
    public async Task ErrorMessages_ExplainTheProblem_WithoutLeakingTheKey()
    {
        var handler = new CapturingHandler { Status = HttpStatusCode.Unauthorized, Response = """{"message":"Invalid API key"}""" };

        StateStoreException? error = null;
        try { await Store(handler).LoadAsync("guard", CancellationToken.None); }
        catch (StateStoreException ex) { error = ex; }

        Assert.NotNull(error);
        Assert.Contains("401", error!.Message);
        Assert.Contains("Invalid API key", error.Message);
        Assert.Contains("SUPABASE_SECRET_KEY", error.Message);
        Assert.False(error.Message.Contains(SecretKey));
    }

    [Fact]
    public async Task MissingTable_PointsToTheSetupSql()
    {
        var handler = new CapturingHandler
        {
            Status = HttpStatusCode.NotFound,
            Response = """{"code":"PGRST205","message":"Could not find the table 'public.bot_state' in the schema cache"}"""
        };

        StateStoreException? error = null;
        try { await Store(handler).SaveAsync("guard", "{}", CancellationToken.None); }
        catch (StateStoreException ex) { error = ex; }

        Assert.NotNull(error);
        Assert.Contains("bot_state", error!.Message);
        Assert.Contains("README", error.Message);
    }
}

public class FileStateStoreTests
{
    [Fact]
    public async Task RoundTrips_AndKeepsTheHistoricalGuardFileName()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"state-{Guid.NewGuid():N}");
        var guardPath = Path.Combine(dir, "guard-state.json");
        try
        {
            var store = new FileStateStore(guardPath);
            Assert.Null(await store.LoadAsync("guard", CancellationToken.None));

            await store.SaveAsync("guard", """{"a":1}""", CancellationToken.None);
            await store.SaveAsync("activity", "[]", CancellationToken.None);

            Assert.True(File.Exists(guardPath));
            Assert.True(File.Exists(Path.Combine(dir, "activity.json")));
            Assert.Equal("""{"a":1}""", await store.LoadAsync("guard", CancellationToken.None));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task UnsafeKeys_AreRejected()
    {
        var store = new FileStateStore(Path.Combine(Path.GetTempPath(), "x", "g.json"));
        var threw = false;
        try { await store.LoadAsync("../../etc/passwd", CancellationToken.None); }
        catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}

public class SupabaseAndHoursConfigTests
{
    private static Func<string, string?> Env(Dictionary<string, string>? extra = null)
    {
        var d = new Dictionary<string, string>
        {
            ["OANDA_API_TOKEN"] = "t", ["OANDA_ACCOUNT_ID"] = "101-001-1-001", ["APPROVER_PASSWORD"] = "a-long-enough-password"
        };
        if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
        return k => d.GetValueOrDefault(k);
    }

    private static string B64(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(string role) => $"{B64("""{"alg":"HS256"}""")}.{B64($$"""{"role":"{{role}}"}""")}.signature";

    private static void Rejected(Dictionary<string, string> extra)
    {
        var threw = false;
        try { AppConfig.FromEnvironment(Env(extra)); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void NoSupabaseSettings_MeansTheLocalFileStore_AndDefaultHours()
    {
        var c = AppConfig.FromEnvironment(Env());
        Assert.Null(c.SupabaseUrl);
        Assert.Null(c.SupabaseSecretKey);
        Assert.Equal("Mon-Fri 09:00-17:00 Australia/Melbourne", c.TradingHours.Describe());
    }

    [Fact]
    public void ValidSupabaseSettings_AreAccepted_AndTheUrlIsTidied()
    {
        var plain = AppConfig.FromEnvironment(Env(new() { ["SUPABASE_URL"] = "https://abcd.supabase.co/", ["SUPABASE_SECRET_KEY"] = "sb_secret_x" }));
        Assert.Equal("https://abcd.supabase.co", plain.SupabaseUrl);
        Assert.Equal("sb_secret_x", plain.SupabaseSecretKey);

        var pasted = AppConfig.FromEnvironment(Env(new() { ["SUPABASE_URL"] = "https://abcd.supabase.co/rest/v1/", ["SUPABASE_SECRET_KEY"] = "sb_secret_x" }));
        Assert.Equal("https://abcd.supabase.co", pasted.SupabaseUrl);
    }

    [Fact]
    public void LocalSupabaseOverPlainHttp_IsAcceptedOnlyOnThisMachine()
    {
        var local = AppConfig.FromEnvironment(Env(new() { ["SUPABASE_URL"] = "http://127.0.0.1:54321", ["SUPABASE_SECRET_KEY"] = "sb_secret_x" }));
        Assert.Equal("http://127.0.0.1:54321", local.SupabaseUrl);

        var localhost = AppConfig.FromEnvironment(Env(new() { ["SUPABASE_URL"] = "http://localhost:54321", ["SUPABASE_SECRET_KEY"] = "sb_secret_x" }));
        Assert.Equal("http://localhost:54321", localhost.SupabaseUrl);
    }

    [Fact]
    public void LegacyServiceRoleKey_IsAccepted()
    {
        var c = AppConfig.FromEnvironment(Env(new() { ["SUPABASE_URL"] = "https://abcd.supabase.co", ["SUPABASE_SECRET_KEY"] = Jwt("service_role") }));
        Assert.NotNull(c.SupabaseSecretKey);
    }

    [Fact]
    public void OnlyOneOfUrlAndKey_IsRejected()
    {
        Rejected(new() { ["SUPABASE_URL"] = "https://abcd.supabase.co" });
        Rejected(new() { ["SUPABASE_SECRET_KEY"] = "sb_secret_x" });
    }

    [Fact]
    public void BadUrls_AreRejected()
    {
        Rejected(new() { ["SUPABASE_URL"] = "http://abcd.supabase.co", ["SUPABASE_SECRET_KEY"] = "sb_secret_x" });
        Rejected(new() { ["SUPABASE_URL"] = "http://127.0.0.1.evil.example.com", ["SUPABASE_SECRET_KEY"] = "sb_secret_x" });
        Rejected(new() { ["SUPABASE_URL"] = "https://abcd.supabase.co/some/path", ["SUPABASE_SECRET_KEY"] = "sb_secret_x" });
        Rejected(new() { ["SUPABASE_URL"] = "not a url", ["SUPABASE_SECRET_KEY"] = "sb_secret_x" });
    }

    [Fact]
    public void PublishableAndAnonKeys_AreRejected()
    {
        Rejected(new() { ["SUPABASE_URL"] = "https://abcd.supabase.co", ["SUPABASE_SECRET_KEY"] = "sb_publishable_abc" });
        Rejected(new() { ["SUPABASE_URL"] = "https://abcd.supabase.co", ["SUPABASE_SECRET_KEY"] = Jwt("anon") });
    }

    [Fact]
    public void TradingHoursSettings_AreReadFromTheEnvironment()
    {
        var always = AppConfig.FromEnvironment(Env(new() { ["TRADING_HOURS"] = "always" }));
        Assert.True(always.TradingHours.Unrestricted);

        var custom = AppConfig.FromEnvironment(Env(new()
        {
            ["TRADING_HOURS"] = "08:00-18:00", ["TRADING_DAYS"] = "Mon-Thu", ["TRADING_TIMEZONE"] = "UTC"
        }));
        Assert.Equal("Mon-Thu 08:00-18:00 UTC", custom.TradingHours.Describe());

        Rejected(new() { ["TRADING_HOURS"] = "nine to five" });
        Rejected(new() { ["TRADING_TIMEZONE"] = "Mars/Olympus" });
    }
}
