using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace ForexHitl;

public sealed class OandaClient : IBrokerClient
{
    private readonly HttpClient _http;
    private readonly string _accountId;

    public OandaClient(HttpClient http, AppConfig config)
    {
        _http = http;
        _accountId = config.OandaAccountId;
    }

    public async Task<AccountSummary> GetAccountAsync(CancellationToken ct)
    {
        using var doc = await GetAsync($"v3/accounts/{_accountId}/summary", ct);
        var acct = doc.RootElement.GetProperty("account");
        var currency = acct.TryGetProperty("currency", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(currency))
            throw new BrokerException("OANDA account summary did not include the account currency; cannot size risk safely.");
        return new AccountSummary(Dec(acct, "NAV"), Dec(acct, "balance"), currency.ToUpperInvariant());
    }

    public async Task<Quote> GetQuoteAsync(string instrument, CancellationToken ct)
    {
        using var doc = await GetAsync($"v3/accounts/{_accountId}/pricing?instruments={instrument}", ct);
        var price = doc.RootElement.GetProperty("prices")[0];
        var time = price.TryGetProperty("time", out var t) && t.GetString() is { } ts ? ParseTime(ts) : DateTime.UtcNow;

        if (!price.TryGetProperty("bids", out var bids) || bids.GetArrayLength() == 0 ||
            !price.TryGetProperty("asks", out var asks) || asks.GetArrayLength() == 0)
            return new Quote(instrument, 0m, 0m, time, Tradeable: false);

        var tradeable = price.TryGetProperty("tradeable", out var tr) && tr.GetBoolean();
        return new Quote(instrument, Dec(bids[0], "price"), Dec(asks[0], "price"), time, tradeable);
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string instrument, string granularity, int count, CancellationToken ct)
    {
        using var doc = await GetAsync(
            $"v3/instruments/{instrument}/candles?granularity={granularity}&count={count}&price=M", ct);

        var list = new List<Candle>();
        foreach (var c in doc.RootElement.GetProperty("candles").EnumerateArray())
        {
            if (!c.GetProperty("complete").GetBoolean()) continue;
            var mid = c.GetProperty("mid");
            list.Add(new Candle(
                ParseTime(c.GetProperty("time").GetString()!),
                Dec(mid, "o"), Dec(mid, "h"), Dec(mid, "l"), Dec(mid, "c")));
        }
        return list;
    }

    public async Task<bool> HasOpenPositionAsync(string instrument, CancellationToken ct)
    {
        using var doc = await GetAsync($"v3/accounts/{_accountId}/openPositions", ct);
        foreach (var p in doc.RootElement.GetProperty("positions").EnumerateArray())
            if (p.GetProperty("instrument").GetString() == instrument) return true;
        return false;
    }

    public async Task<OrderResult> PlaceMarketOrderAsync(TradeProposal p, CancellationToken ct)
    {
        var signedUnits = p.Side == Side.Buy ? p.Units : -p.Units;
        var body = new
        {
            order = new
            {
                type = "MARKET",
                instrument = p.Instrument,
                units = signedUnits.ToString(CultureInfo.InvariantCulture),
                timeInForce = "FOK",
                positionFill = "DEFAULT",
                stopLossOnFill = new { price = Fmt(p.StopLoss), timeInForce = "GTC" },
                takeProfitOnFill = new { price = Fmt(p.TakeProfit), timeInForce = "GTC" }
            }
        };

        using var resp = await _http.PostAsJsonAsync($"v3/accounts/{_accountId}/orders", body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return new OrderResult(false, $"HTTP {(int)resp.StatusCode} (unparseable body)", null); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("orderFillTransaction", out var fill))
            {
                var tradeId = fill.TryGetProperty("tradeOpened", out var opened) &&
                              opened.TryGetProperty("tradeID", out var id) ? id.GetString() : null;
                var price = fill.TryGetProperty("price", out var pr) ? pr.GetString() : "?";
                return new OrderResult(true, $"Filled @ {price}", tradeId);
            }

            var reason = root.TryGetProperty("orderCancelTransaction", out var cancel) &&
                         cancel.TryGetProperty("reason", out var r) ? r.GetString()
                       : root.TryGetProperty("errorMessage", out var em) ? em.GetString()
                       : $"HTTP {(int)resp.StatusCode}";
            return new OrderResult(false, $"Order not filled: {reason}", null);
        }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        // Reads are idempotent, so ONE quick retry after a connection-level failure (dropped or reset
        // connection, brief network blip) is safe. Order placement (POST) is deliberately never retried.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(path, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new BrokerException($"OANDA {(int)resp.StatusCode} on GET {path.Split('?')[0]}: {body}");
                return JsonDocument.Parse(body);
            }
            catch (HttpRequestException) when (attempt == 1 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private static decimal Dec(JsonElement e, string name) =>
        decimal.Parse(e.GetProperty(name).GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Fmt(decimal price) => price.ToString("F5", CultureInfo.InvariantCulture);

    /// <summary>OANDA sends RFC3339 with nanoseconds, which .NET cannot parse; seconds precision is enough.</summary>
    internal static DateTime ParseTime(string s)
    {
        var dot = s.IndexOf('.');
        var core = (dot > 0 ? s[..dot] : s).TrimEnd('Z');
        return DateTime.SpecifyKind(
            DateTime.ParseExact(core, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }
}
