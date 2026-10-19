using ForexHitl;

namespace ForexHitl.Tests;

internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public class SessionAuthTests
{
    private const string Password = "correct-horse-battery";

    [Fact]
    public void CorrectPassword_GivesWorkingUniqueTokens()
    {
        var auth = new SessionAuth(Password, new FakeClock());
        var a = auth.Login(Password);
        var b = auth.Login(Password);

        Assert.True(a.Ok);
        Assert.True(auth.IsValid(a.Token));
        Assert.True(auth.IsValid(b.Token));
        Assert.False(a.Token == b.Token);
    }

    [Fact]
    public void WrongOrMissingPassword_IsRejected()
    {
        var auth = new SessionAuth(Password, new FakeClock());
        Assert.False(auth.Login("nope").Ok);
        Assert.False(auth.Login(null).Ok);
        Assert.False(auth.Login("").Ok);
        Assert.False(auth.IsValid(null));
        Assert.False(auth.IsValid("made-up-token"));
    }

    [Fact]
    public void FiveFailures_LockOutEvenTheCorrectPassword_ThenRecover()
    {
        var clock = new FakeClock();
        var auth = new SessionAuth(Password, clock);

        for (var i = 0; i < SessionAuth.MaxFailures - 1; i++)
            Assert.False(auth.Login("bad").Locked);
        Assert.True(auth.Login("bad").Locked);

        var during = auth.Login(Password);
        Assert.False(during.Ok);
        Assert.True(during.Locked);

        clock.Advance(SessionAuth.Lockout + TimeSpan.FromSeconds(1));
        Assert.True(auth.Login(Password).Ok);
    }

    [Fact]
    public void SuccessfulLogin_ResetsTheFailureCount()
    {
        var auth = new SessionAuth(Password, new FakeClock());
        for (var i = 0; i < SessionAuth.MaxFailures - 1; i++) auth.Login("bad");
        Assert.True(auth.Login(Password).Ok);
        for (var i = 0; i < SessionAuth.MaxFailures - 1; i++) Assert.False(auth.Login("bad").Locked);
    }

    [Fact]
    public void Sessions_ExpireAndCanBeRevoked()
    {
        var clock = new FakeClock();
        var auth = new SessionAuth(Password, clock);

        var expiring = auth.Login(Password).Token;
        var revoked = auth.Login(Password).Token;

        auth.Logout(revoked);
        Assert.False(auth.IsValid(revoked));
        Assert.True(auth.IsValid(expiring));

        clock.Advance(SessionAuth.SessionLifetime + TimeSpan.FromMinutes(1));
        Assert.False(auth.IsValid(expiring));
    }
}

public class StateHolderTests
{
    private static TradeProposal Proposal(string id, string instrument, DateTime created) =>
        new(id, instrument, Side.Buy, 1.1m, 1.099m, 1.102m, 10m, 50_000, 50m, created, "test");

    [Fact]
    public void Store_HandsEachProposalOutExactlyOnce()
    {
        var store = new PendingTradeStore();
        store.Add(Proposal("a1", "EUR_USD", DateTime.UtcNow));

        Assert.True(store.HasPending("EUR_USD"));
        Assert.False(store.HasPending("GBP_USD"));
        Assert.True(store.TryTake("a1", out var first));
        Assert.Equal("a1", first.Id);
        Assert.False(store.TryTake("a1", out _));   // a double-tap on Approve cannot place two orders
        Assert.False(store.HasPending("EUR_USD"));
    }

    [Fact]
    public void Store_ExpiresOnlyOldProposals()
    {
        var store = new PendingTradeStore();
        var now = new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Add(Proposal("old", "EUR_USD", now.AddMinutes(-10)));
        store.Add(Proposal("new", "GBP_USD", now));

        var expired = store.TakeExpired(now - ExecutionAgent.ProposalTtl);

        Assert.Equal(1, expired.Count);
        Assert.Equal("old", expired[0].Id);
        Assert.Equal(1, store.Snapshot().Count);
    }

    [Fact]
    public void ActivityLog_IsNewestFirst_AndCapped()
    {
        var log = new ActivityLog(new FakeClock());
        for (var i = 0; i < 60; i++) log.Add("test", $"event {i}");

        var items = log.Snapshot();
        Assert.Equal(50, items.Count);
        Assert.Equal("event 59", items[0].Text);
        Assert.Equal("event 10", items[^1].Text);
    }
}

public class AppConfigTests
{
    private static Func<string, string?> Env(Dictionary<string, string> extra)
    {
        var d = new Dictionary<string, string>
        {
            ["OANDA_API_TOKEN"] = "t", ["OANDA_ACCOUNT_ID"] = "101-001-1-001",
            ["APPROVER_PASSWORD"] = "a-long-enough-password"
        };
        foreach (var (k, v) in extra) d[k] = v;
        return k => d.GetValueOrDefault(k);
    }

    [Fact]
    public void DefaultsToPracticeMajorsAndNoPush()
    {
        var c = AppConfig.FromEnvironment(Env(new()));
        Assert.False(c.UseLive);
        Assert.Contains("practice", c.OandaBaseUrl);
        Assert.Equal(new[] { "EUR_USD", "GBP_USD" }, c.Instruments);
        Assert.Null(c.NtfyTopic);
    }

    [Fact]
    public void Live_RequiresExplicitSecondOptIn()
    {
        Assert.Throws<InvalidOperationException>(() => AppConfig.FromEnvironment(Env(new() { ["OANDA_ENVIRONMENT"] = "live" })));
        var ok = AppConfig.FromEnvironment(Env(new() { ["OANDA_ENVIRONMENT"] = "live", ["ALLOW_LIVE_TRADING"] = "true" }));
        Assert.True(ok.UseLive);
    }

    [Fact]
    public void MissingSecrets_FailFast()
    {
        var e = Assert.Throws<InvalidOperationException>(() => AppConfig.FromEnvironment(_ => null));
        Assert.Contains("Missing required environment variable", e.Message);
    }

    [Fact]
    public void ShortPassword_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => AppConfig.FromEnvironment(Env(new() { ["APPROVER_PASSWORD"] = "short" })));
    }

    [Theory]
    [InlineData("tooshort")]
    [InlineData("has spaces in it but is long enough")]
    public void WeakOrMalformedNtfyTopic_IsRejected(string topic)
    {
        Assert.Throws<InvalidOperationException>(() => AppConfig.FromEnvironment(Env(new() { ["NTFY_TOPIC"] = topic })));
    }

    [Fact]
    public void GoodNtfyTopic_IsAccepted()
    {
        var c = AppConfig.FromEnvironment(Env(new() { ["NTFY_TOPIC"] = "forex-7f3a9c1e5b2d4a68" }));
        Assert.Equal("forex-7f3a9c1e5b2d4a68", c.NtfyTopic);
        Assert.Equal("https://ntfy.sh", c.NtfyServer);
    }

    [Fact]
    public void UnsupportedInstrument_IsRejected()
    {
        Assert.Throws<NotSupportedException>(() => AppConfig.FromEnvironment(Env(new() { ["INSTRUMENTS"] = "USD_JPY" })));
    }
}
