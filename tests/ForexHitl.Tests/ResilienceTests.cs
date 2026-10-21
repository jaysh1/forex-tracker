using System.Net;
using ForexHitl;

namespace ForexHitl.Tests;

internal sealed class ScriptedHandler : HttpMessageHandler
{
    public int Calls;
    public int FailFirst;                 // throw a connection-level error for this many calls
    public bool AlwaysFail;
    public HttpStatusCode? FirstStatus;
    public string Json = """{"account":{"NAV":"100000.0000","balance":"100000.0000","currency":"AUD"}}""";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        if (Calls == 1 && FirstStatus is { } status)
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("temporary upstream failure") });
        if (AlwaysFail || Calls <= FailFirst)
            throw new HttpRequestException("An error occurred while sending the request.",
                new IOException("Unable to read data from the transport connection: connection forcibly closed."));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json) });
    }
}

public class OandaResilienceTests
{
    private static OandaClient Client(HttpMessageHandler handler)
    {
        var env = new Dictionary<string, string>
        {
            ["OANDA_API_TOKEN"] = "t", ["OANDA_ACCOUNT_ID"] = "101-001-1-001", ["APPROVER_PASSWORD"] = "a-long-enough-password"
        };
        var config = AppConfig.FromEnvironment(k => env.GetValueOrDefault(k));
        return new OandaClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, config);
    }

    [Fact]
    public async Task OneDroppedConnection_OnARead_IsRetriedAndSucceeds()
    {
        var handler = new ScriptedHandler { FailFirst = 1 };
        var acct = await Client(handler).GetAccountAsync(CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.Equal("AUD", acct.Currency);
        Assert.Equal(100_000m, acct.Nav);
    }

    [Fact]
    public async Task PersistentFailure_OnARead_StopsAfterThreeAttempts()
    {
        var handler = new ScriptedHandler { AlwaysFail = true };

        var threw = false;
        try { await Client(handler).GetAccountAsync(CancellationToken.None); }
        catch (HttpRequestException) { threw = true; }

        Assert.True(threw);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task GatewayTimeout_OnARead_IsRetriedAndSucceeds()
    {
        var handler = new ScriptedHandler { FirstStatus = HttpStatusCode.GatewayTimeout };
        var acct = await Client(handler).GetAccountAsync(CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(100_000m, acct.Nav);
    }

    [Fact]
    public async Task PersistentGatewayTimeout_OnARead_StopsAfterThreeAttempts()
    {
        var persistent = new PersistentStatusHandler(HttpStatusCode.GatewayTimeout);
        BrokerException? failure = null;
        try { await Client(persistent).GetAccountAsync(CancellationToken.None); }
        catch (BrokerException ex) { failure = ex; }

        Assert.NotNull(failure);
        Assert.Contains("after 3 attempts", failure.Message);
        Assert.Equal(3, persistent.Calls);
    }

    [Fact]
    public async Task OrderPlacement_IsNeverRetried()
    {
        var handler = new ScriptedHandler { AlwaysFail = true };
        var proposal = new TradeProposal("a1", "EUR_USD", Side.Buy, 1.1m, 1.099m, 1.102m, 10m, 1_000, 1m, DateTime.UtcNow, "t");

        var threw = false;
        try { await Client(handler).PlaceMarketOrderAsync(proposal, CancellationToken.None); }
        catch (HttpRequestException) { threw = true; }

        Assert.True(threw);
        Assert.Equal(1, handler.Calls);     // a retried order could be placed twice
    }

    [Fact]
    public void Describe_ShowsTheWholeCauseChain()
    {
        var ex = new HttpRequestException("An error occurred while sending the request.",
            new IOException("connection forcibly closed"));

        Assert.Equal("An error occurred while sending the request. -> connection forcibly closed",
            TechnicalAnalyticsAgent.Describe(ex));
        Assert.Equal("plain", TechnicalAnalyticsAgent.Describe(new InvalidOperationException("plain")));
    }
}

internal sealed class PersistentStatusHandler(HttpStatusCode status) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("upstream failure") });
    }
}
