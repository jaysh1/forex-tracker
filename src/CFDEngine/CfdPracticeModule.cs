using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace CFDEngine;

/// <summary>CFD-specific settings and risk parameters. Deliberately separate from Forex AppConfig.</summary>
public sealed record CfdSettings(IReadOnlyList<string> Instruments, CfdTradingWindow TradingHours, decimal MaxRiskPerTrade,
    IReadOnlyDictionary<string, decimal> MaxSpreadPoints, IReadOnlyDictionary<string, decimal> MaxApprovalDriftPoints,
    IReadOnlyDictionary<string, decimal> QuoteCurrencyPerPointPerUnit)
{
    public bool Enabled => Instruments.Count > 0;

    public static CfdSettings FromEnvironment(bool forexLive, Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;
        var instruments = (get("CFD_INSTRUMENTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant()).Distinct().ToArray();
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "AU200_AUD", "SPX500_USD" };
        if (instruments.Any(x => !allowed.Contains(x)))
            throw new InvalidOperationException("CFD_INSTRUMENTS currently supports AU200_AUD and SPX500_USD.");
        if (forexLive && instruments.Length > 0)
            throw new InvalidOperationException("CFD practice trading is disabled on live accounts.");

        var spreadCaps = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var driftCaps = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var pointValues = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var instrument in instruments)
        {
            var key = $"CFD_MAX_SPREAD_POINTS_{instrument}";
            var raw = get(key);
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var cap) || cap <= 0)
                throw new InvalidOperationException($"{key} must be set to a positive price-point limit before enabling {instrument}.");
            spreadCaps[instrument] = cap;
            var driftKey = $"CFD_MAX_APPROVAL_DRIFT_POINTS_{instrument}";
            var rawDrift = get(driftKey);
            if (!decimal.TryParse(rawDrift, NumberStyles.Number, CultureInfo.InvariantCulture, out var drift) || drift <= 0)
                throw new InvalidOperationException($"{driftKey} must be set to a positive price-point limit before enabling {instrument}.");
            driftCaps[instrument] = drift;
            var valueKey = $"CFD_QUOTE_CURRENCY_PER_POINT_PER_UNIT_{instrument}";
            var rawValue = get(valueKey);
            if (!decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var pointValue) || pointValue <= 0)
                throw new InvalidOperationException($"{valueKey} must be set to a positive value from the OANDA contract specification before enabling {instrument}.");
            pointValues[instrument] = pointValue;
        }

        var rawRisk = get("CFD_MAX_RISK_PER_TRADE");
        var risk = string.IsNullOrWhiteSpace(rawRisk) ? CfdRiskLimits.DefaultMaxRiskPerTrade :
            decimal.TryParse(rawRisk, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed : throw new InvalidOperationException("CFD_MAX_RISK_PER_TRADE must be positive.");
        var hours = CfdTradingWindow.Parse(get("CFD_TRADING_HOURS") ?? "always",
            get("CFD_TRADING_DAYS") ?? "Mon-Fri", get("CFD_TRADING_TIMEZONE"));
        return new CfdSettings(instruments, hours, risk, spreadCaps, driftCaps, pointValues);
    }
}

public sealed record CfdTradeProposal(string Id, string Instrument, CfdSide Side, long Units,
    decimal Entry, decimal StopLoss, decimal TakeProfit, decimal StopDistancePoints,
    decimal MaxLoss, string Currency, DateTime CreatedUtc, string Rationale,
    int PricePrecision, decimal PipSize, decimal MarginRate);

public static class CfdRiskLimits
{
    public const decimal RiskPerTradePercent = 0.5m;
    public const decimal DailyDrawdownPercent = 1.5m;
    public const decimal DailyProfitTargetPercent = 1m;
    public static readonly TimeSpan DrawdownLockout = TimeSpan.FromHours(24);
    public const decimal DefaultMaxRiskPerTrade = 50m;
    public const decimal MaxLeverage = 10m;
    public const decimal StopAtrMultiple = 1.5m;
    public const decimal RewardToRisk = 2m;
}

public static class CfdRiskSizer
{
    public static long CalculateUnits(CfdContract contract, decimal equity, decimal stopDistancePoints, decimal quoteCurrencyPerPointPerUnit,
        decimal entryPrice, decimal quoteToAccount, decimal maxRiskPerTrade)
    {
        if (equity <= 0 || stopDistancePoints <= 0 || entryPrice <= 0 || quoteToAccount <= 0 ||
            quoteCurrencyPerPointPerUnit <= 0 || contract.PipSize <= 0 || contract.MarginRate <= 0 || maxRiskPerTrade <= 0)
            throw new ArgumentException("CFD equity, stop, price, conversion, margin, and risk cap must be positive.");
        var budget = Math.Min(equity * CfdRiskLimits.RiskPerTradePercent / 100m, maxRiskPerTrade);
        var byRisk = Math.Floor(budget / (stopDistancePoints * quoteCurrencyPerPointPerUnit * quoteToAccount));
        var maxLeverage = Math.Min(CfdRiskLimits.MaxLeverage, 1m / contract.MarginRate);
        var byMargin = Math.Floor(equity * maxLeverage / (entryPrice * quoteToAccount));
        var units = Math.Min(Math.Min(byRisk, byMargin), contract.MaximumUnits);
        return units < contract.MinimumUnits || units > long.MaxValue ? 0 : (long)units;
    }

    public static decimal MaxLoss(long units, decimal stopDistancePoints, decimal quoteCurrencyPerPointPerUnit, decimal quoteToAccount) =>
        units * stopDistancePoints * quoteCurrencyPerPointPerUnit * quoteToAccount;
}

public sealed record CfdStatusDto(bool Enabled, string Mode, IReadOnlyList<string> Instruments,
    DateTime? UpdatedUtc, decimal? Equity, string? Currency, decimal? DayPnl, bool CanTrade,
    string? Reason, string TradingHours, IReadOnlyList<CfdPendingDto> Pending);

public sealed record CfdPendingDto(string Id, string Instrument, string Side, long Units,
    decimal Entry, decimal StopLoss, decimal TakeProfit, decimal StopDistancePoints, decimal MaxLoss,
    string Currency, DateTime CreatedUtc, DateTime ExpiresUtc, string Rationale, int PricePrecision);

/// <summary>Algo plug-in point: a CFD strategy consumes completed bars and returns a signal.</summary>
/// <summary>Initial practice strategy. New strategies can be added without editing the Forex strategy.</summary>
public sealed class CfdTrendPullbackStrategy : ICfdStrategy
{
    public string Name => "EMA trend + RSI pullback";
    public CfdSignal? Evaluate(IReadOnlyList<CfdCandle> m5, IReadOnlyList<CfdCandle> m15) => CfdSignalEngine.Evaluate(m5, m15);
}

/// <summary>CFD-owned signal logic. It can evolve without changing or calling the Forex strategy.</summary>
public static class CfdSignalEngine
{
    public static CfdSignal? Evaluate(IReadOnlyList<CfdCandle> m5, IReadOnlyList<CfdCandle> m15)
    {
        const int fastPeriod = 50, slowPeriod = 200, rsiPeriod = 14, atrPeriod = 14;
        if (m15.Count < slowPeriod || m5.Count < Math.Max(rsiPeriod, atrPeriod) + 2) return null;
        var fast = CfdIndicators.Ema(m15.Select(c => c.Close).ToList(), fastPeriod)[^1];
        var slow = CfdIndicators.Ema(m15.Select(c => c.Close).ToList(), slowPeriod)[^1];
        if (fast is not { } fastValue || slow is not { } slowValue) return null;
        var rsi = CfdIndicators.Rsi(m5.Select(c => c.Close).ToList(), rsiPeriod);
        var atr = CfdIndicators.Atr(m5, atrPeriod)[^1];
        if (rsi[^2] is not { } previous || rsi[^1] is not { } current || atr is not { } averageRange || averageRange <= 0) return null;
        if (fastValue > slowValue && previous < 30m && current >= 30m)
            return new CfdSignal(CfdSide.Buy, averageRange, $"CFD 15m uptrend (EMA50 {fastValue:F2} > EMA200 {slowValue:F2}); 5m RSI {previous:F1} -> {current:F1} leaving oversold");
        if (fastValue < slowValue && previous > 70m && current <= 70m)
            return new CfdSignal(CfdSide.Sell, averageRange, $"CFD 15m downtrend (EMA50 {fastValue:F2} < EMA200 {slowValue:F2}); 5m RSI {previous:F1} -> {current:F1} leaving overbought");
        return null;
    }
}

/// <summary>OANDA v20 CFD adapter. Contract precision, minimum size and margin come from the account API.</summary>
public sealed class CfdOandaClient : ICfdBroker
{
    private readonly HttpClient _http;
    private readonly string _accountId;

    public CfdOandaClient(HttpClient http, string accountId)
    {
        _http = http;
        _accountId = accountId;
    }

    public async Task<CfdAccount> GetAccountAsync(CancellationToken ct)
    {
        using var d = await GetAsync($"v3/accounts/{_accountId}/summary", ct);
        var a = d.RootElement.GetProperty("account");
        var currency = a.GetProperty("currency").GetString()!.ToUpperInvariant();
        return new CfdAccount(Dec(a, "NAV"), Dec(a, "balance"), currency);
    }

    public async Task<CfdContract> GetContractAsync(string instrument, CancellationToken ct)
    {
        using var d = await GetAsync($"v3/accounts/{_accountId}/instruments?instruments={instrument}", ct);
        var list = d.RootElement.GetProperty("instruments");
        if (list.GetArrayLength() != 1) throw new CfdBrokerException($"OANDA practice account does not offer {instrument}.");
        var i = list[0];
        if (i.GetProperty("type").GetString() != "CFD")
            throw new CfdBrokerException($"OANDA identifies {instrument} as a non-CFD instrument.");
        var precision = i.GetProperty("tradeUnitsPrecision").GetInt32();
        if (precision != 0) throw new CfdBrokerException($"{instrument} allows fractional units; this version only supports whole CFD units.");
        var name = i.GetProperty("name").GetString()!;
        if (name != instrument) throw new CfdBrokerException($"OANDA returned unexpected contract {name} for {instrument}.");
        var marginRate = Dec(i, "marginRate");
        var minUnits = Dec(i, "minimumTradeSize");
        var maxOrderUnits = i.TryGetProperty("maximumOrderUnits", out _) ? Dec(i, "maximumOrderUnits") : long.MaxValue;
        var maxPositionUnits = i.TryGetProperty("maximumPositionSize", out _) ? Dec(i, "maximumPositionSize") : long.MaxValue;
        var maxUnits = Math.Min(maxOrderUnits, maxPositionUnits);
        if (marginRate <= 0 || minUnits <= 0) throw new CfdBrokerException($"OANDA returned invalid margin or minimum size for {instrument}.");
        var contract = new CfdContract(name, i.GetProperty("displayPrecision").GetInt32(), precision,
            (decimal)Math.Pow(10, i.GetProperty("pipLocation").GetInt32()),
            minUnits, marginRate, name.Split('_').Last(), maxUnits <= 0 ? long.MaxValue : maxUnits);
        return contract;
    }

    public async Task<CfdQuote> GetQuoteAsync(string instrument, CancellationToken ct)
    {
        using var d = await GetAsync($"v3/accounts/{_accountId}/pricing?instruments={instrument}", ct);
        var prices = d.RootElement.GetProperty("prices");
        if (prices.GetArrayLength() == 0) return new CfdQuote(instrument, 0, 0, DateTime.UtcNow, false);
        var p = prices[0];
        var timeText = p.GetProperty("time").GetString()!;
        var time = ParseTime(timeText);
        if (!p.TryGetProperty("bids", out var bids) || bids.GetArrayLength() == 0 ||
            !p.TryGetProperty("asks", out var asks) || asks.GetArrayLength() == 0)
            return new CfdQuote(instrument, 0, 0, time, false);
        var tradeable = p.TryGetProperty("tradeable", out var t) ? t.ValueKind == JsonValueKind.True :
            p.TryGetProperty("status", out var s) && string.Equals(s.GetString(), "tradeable", StringComparison.OrdinalIgnoreCase);
        return new CfdQuote(instrument, Dec(bids[0], "price"), Dec(asks[0], "price"), time, tradeable);
    }

    public async Task<IReadOnlyList<CfdCandle>> GetCandlesAsync(string instrument, string granularity, int count, CancellationToken ct)
    {
        using var d = await GetAsync($"v3/instruments/{instrument}/candles?granularity={granularity}&count={count}&price=M", ct);
        return d.RootElement.GetProperty("candles").EnumerateArray().Where(c => c.GetProperty("complete").GetBoolean())
            .Select(c =>
            {
                var mid = c.GetProperty("mid");
                return new CfdCandle(ParseTime(c.GetProperty("time").GetString()!), Dec(mid,"o"), Dec(mid,"h"), Dec(mid,"l"), Dec(mid,"c"));
            }).ToList();
    }

    public async Task<bool> HasOpenPositionAsync(string instrument, CancellationToken ct)
    {
        using var d = await GetAsync($"v3/accounts/{_accountId}/openPositions", ct);
        return d.RootElement.GetProperty("positions").EnumerateArray().Any(p => p.GetProperty("instrument").GetString() == instrument);
    }

    public async Task<decimal> CurrencyToAccountAsync(string currency, string accountCurrency, CancellationToken ct)
    {
        currency = currency.ToUpperInvariant(); accountCurrency = accountCurrency.ToUpperInvariant();
        if (currency == accountCurrency) return 1m;
        var fromUsd = currency == "USD" ? 1m : 1m / await UsdToCurrencyAsync(currency, ct);
        var accountPerUsd = accountCurrency == "USD" ? 1m : await UsdToCurrencyAsync(accountCurrency, ct);
        return fromUsd * accountPerUsd;
    }

    private async Task<decimal> UsdToCurrencyAsync(string currency, CancellationToken ct)
    {
        var baseCurrency = new HashSet<string> { "AUD", "EUR", "GBP", "NZD" }.Contains(currency);
        var pair = baseCurrency ? $"{currency}_USD" : $"USD_{currency}";
        var q = await GetQuoteAsync(pair, ct);
        if (!q.Tradeable || q.Bid <= 0 || q.Ask <= 0)
            throw new CfdBrokerException($"Could not obtain a conversion rate for {currency}.");
        return baseCurrency ? 1m / q.Bid : q.Ask;
    }

    public async Task<CfdOrderResult> PlaceMarketOrderAsync(CfdTradeProposal p, CancellationToken ct)
    {
        var units = p.Side == CfdSide.Buy ? p.Units : -p.Units;
        var body = new { order = new { type = "MARKET", instrument = p.Instrument,
            units = units.ToString(CultureInfo.InvariantCulture), timeInForce = "FOK", positionFill = "DEFAULT",
            stopLossOnFill = new { price = Fmt(p.StopLoss, p.PricePrecision), timeInForce = "GTC" },
            takeProfitOnFill = new { price = Fmt(p.TakeProfit, p.PricePrecision), timeInForce = "GTC" } } };
        using var response = await _http.PostAsJsonAsync($"v3/accounts/{_accountId}/orders", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var d = JsonDocument.Parse(text);
        var root = d.RootElement;
        if (root.TryGetProperty("orderFillTransaction", out var fill))
        {
            var id = fill.TryGetProperty("tradeOpened", out var opened) && opened.TryGetProperty("tradeID", out var tid) ? tid.GetString() : null;
            var price = fill.TryGetProperty("price", out var px) ? px.GetString() : "?";
            return new CfdOrderResult(true, $"Filled @ {price}", id);
        }
        var reason = root.TryGetProperty("orderCancelTransaction", out var cancel) && cancel.TryGetProperty("reason", out var r)
            ? r.GetString() : root.TryGetProperty("errorMessage", out var err) ? err.GetString() : $"HTTP {(int)response.StatusCode}";
        return new CfdOrderResult(false, $"Order not filled: {reason}", null);
    }

    public Task<CfdOrderResult> PlacePracticeOrderAsync(CfdTradeProposal request, CancellationToken cancellationToken) =>
        PlaceMarketOrderAsync(request, cancellationToken);

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new CfdBrokerException($"OANDA CFD request failed (HTTP {(int)response.StatusCode}).");
        return JsonDocument.Parse(body);
    }

    private static decimal Dec(JsonElement e, string key) => decimal.Parse(e.GetProperty(key).GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static string Fmt(decimal value, int precision) => value.ToString($"F{precision}", CultureInfo.InvariantCulture);
    private static DateTime ParseTime(string value)
    {
        var dot = value.IndexOf('.'); var core = (dot > 0 ? value[..dot] : value).TrimEnd('Z');
        return DateTime.SpecifyKind(DateTime.ParseExact(core, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }
}

/// <summary>Independent CFD practice scanner and approval path. Forex strategy and execution code remain untouched.</summary>
public sealed class CfdPracticeAgent : BackgroundService
{
    private static readonly TimeSpan ProposalLifetime = TimeSpan.FromMinutes(5);
    private readonly CfdSettings _settings;
    private readonly ICfdBroker _broker;
    private readonly ICfdNewsCalendar _news;
    private readonly ICfdStrategy _strategy;
    private readonly CfdDailyRiskGuard _guard;
    private readonly CfdRiskPersistence _persistence;
    private readonly TimeProvider _time;
    private readonly ILogger<CfdPracticeAgent> _log;
    private readonly ConcurrentDictionary<string, CfdTradeProposal> _pending = new();
    private readonly Dictionary<string, DateTime> _lastBars = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime? _updated;
    private decimal? _equity, _dayPnl;
    private string? _currency, _reason;
    private bool _canTrade;

    public CfdPracticeAgent(CfdSettings settings, ICfdBroker broker, ICfdNewsCalendar news, ICfdStrategy strategy,
        CfdDailyRiskGuard guard, CfdRiskPersistence persistence, TimeProvider time, ILogger<CfdPracticeAgent> log)
    { _settings=settings; _broker=broker; _news=news; _strategy=strategy; _guard=guard; _persistence=persistence; _time=time; _log=log; }

    internal void QueueProposal(CfdTradeProposal proposal) => _pending[proposal.Id] = proposal;

    public CfdStatusDto Snapshot() => new(_settings.Enabled, "practice", _settings.Instruments, _updated,
        _equity, _currency, _dayPnl, _canTrade && _settings.Enabled, _reason, _settings.TradingHours.Describe(),
        _pending.Values.OrderBy(p=>p.CreatedUtc).Select(p=>new CfdPendingDto(p.Id,p.Instrument,p.Side.ToString(),p.Units,
            p.Entry,p.StopLoss,p.TakeProfit,p.StopDistancePoints,p.MaxLoss,p.Currency,p.CreatedUtc,
            p.CreatedUtc+ProposalLifetime,p.Rationale,p.PricePrecision)).ToList());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _reason = ex.Message; _log.LogError(ex, "CFD practice scan failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var account = await _broker.GetAccountAsync(ct);
        await _persistence.EnsureLoadedAsync(ct);
        var status = _guard.Evaluate(account.Equity, _time.GetUtcNow().UtcDateTime);
        await _persistence.FlushAsync(ct);
        _equity=account.Equity; _currency=account.Currency; _dayPnl=status.DayPnl; _canTrade=status.CanTrade;
        _reason=status.CanTrade ? null : _persistence.LastProblem ?? status.Reason;
        _updated=_time.GetUtcNow().UtcDateTime;
        foreach (var p in _pending.Values.Where(p=>_time.GetUtcNow().UtcDateTime-p.CreatedUtc>ProposalLifetime)) _pending.TryRemove(p.Id,out _);
        if (!status.CanTrade || !_settings.TradingHours.IsOpen(_time.GetUtcNow().UtcDateTime)) return;
        foreach (var instrument in _settings.Instruments)
        {
            try
            {
                if (_pending.Values.Any(p=>p.Instrument==instrument) || await _broker.HasOpenPositionAsync(instrument,ct)) continue;
                var m5=await _broker.GetCandlesAsync(instrument,"M5",150,ct);
                if (m5.Count==0) continue;
                var latest=m5[^1].TimeUtc;
                if (!_lastBars.TryGetValue(instrument,out var seen)) { _lastBars[instrument]=latest; continue; }
                if (latest<=seen) continue;
                _lastBars[instrument]=latest;
                var m15=await _broker.GetCandlesAsync(instrument,"M15",500,ct);
                var signal=_strategy.Evaluate(m5,m15);
                if (signal is null) continue;
                var contract=await _broker.GetContractAsync(instrument,ct);
                var newsBlock=await NewsBlockAsync(contract.QuoteCurrency, _time.GetUtcNow().UtcDateTime, ct);
                if (newsBlock is not null) { _reason=newsBlock; continue; }
                var quote=await _broker.GetQuoteAsync(instrument,ct);
                if (!quote.Tradeable || quote.Bid<=0 || quote.Ask<=0) continue;
                var spreadPoints=quote.Ask-quote.Bid;
                if (spreadPoints>_settings.MaxSpreadPoints[instrument]) continue;
                var conversion=await _broker.CurrencyToAccountAsync(contract.QuoteCurrency,account.Currency,ct);
                var stopDistance=signal.Atr*CfdRiskLimits.StopAtrMultiple;
                var pointValue=_settings.QuoteCurrencyPerPointPerUnit[instrument];
                var units=CfdRiskSizer.CalculateUnits(contract,account.Equity,stopDistance,pointValue,quote.Ask,conversion,_settings.MaxRiskPerTrade);
                if (units<contract.MinimumUnits || units<=0) continue;
                var entry=signal.Side==CfdSide.Buy?quote.Ask:quote.Bid;
                var dir=signal.Side==CfdSide.Buy?1m:-1m;
                var proposal=new CfdTradeProposal(Guid.NewGuid().ToString("N")[..8],instrument,signal.Side,units,
                    entry,entry-dir*stopDistance,entry+dir*stopDistance*CfdRiskLimits.RewardToRisk,
                    stopDistance,Math.Round(CfdRiskSizer.MaxLoss(units,stopDistance,pointValue,conversion),2),account.Currency,
                    _time.GetUtcNow().UtcDateTime,$"{_strategy.Name}: {signal.Rationale}",contract.PricePrecision,contract.PipSize,contract.MarginRate);
                _pending.TryAdd(proposal.Id,proposal);
            }
            catch (Exception ex) { _reason=$"{instrument}: {ex.Message}"; _log.LogWarning(ex,"CFD scan failed for {Instrument}",instrument); }
        }
    }

    public async Task<CfdDecisionResult> DecideAsync(string action,string id,CancellationToken ct)
    {
        if (!_settings.Enabled) return new(false,"CFD practice trading is disabled.");
        if (action is not ("approve" or "reject")) return new(false,"Invalid action.");
        await _gate.WaitAsync(ct);
        try
        {
            if (!_pending.TryRemove(id,out var p)) return new(false,"Already handled or expired.");
            if (action=="reject") return new(false,"Rejected.");
            var now=_time.GetUtcNow().UtcDateTime;
            if (now-p.CreatedUtc>ProposalLifetime) return new(false,"Proposal expired.");
            if (!_settings.TradingHours.IsOpen(now)) return new(false,$"Outside CFD practice hours ({_settings.TradingHours.Describe()}).");
            var account=await _broker.GetAccountAsync(ct);
            await _persistence.EnsureLoadedAsync(ct);
            var guard=_guard.Evaluate(account.Equity,now); await _persistence.FlushAsync(ct);
            if (!guard.CanTrade) return new(false,$"Trading locked: {guard.Reason}");
            if (await _broker.HasOpenPositionAsync(p.Instrument,ct)) return new(false,"A position in this CFD is already open.");
            var contract=await _broker.GetContractAsync(p.Instrument,ct);
            var newsBlock=await NewsBlockAsync(contract.QuoteCurrency, now, ct);
            if (newsBlock is not null) return new(false,newsBlock);
            var quote=await _broker.GetQuoteAsync(p.Instrument,ct);
            if (!quote.Tradeable || quote.Bid<=0 || quote.Ask<=0) return new(false,"CFD market is closed or has no valid price.");
            var liveSpread=quote.Ask-quote.Bid;
            if (liveSpread>_settings.MaxSpreadPoints[p.Instrument])
                return new(false,$"Spread widened to {liveSpread:F2} points; limit is {_settings.MaxSpreadPoints[p.Instrument]:F2}. Wait for a new proposal.");
            var live=p.Side==CfdSide.Buy?quote.Ask:quote.Bid;
            var drift=Math.Abs(live-p.Entry);
            if (drift>_settings.MaxApprovalDriftPoints[p.Instrument])
                return new(false,$"Price moved {drift:F2} points; wait for a new proposal.");
            var conversion=await _broker.CurrencyToAccountAsync(contract.QuoteCurrency,account.Currency,ct);
            var pointValue=_settings.QuoteCurrencyPerPointPerUnit[p.Instrument];
            var byRiskAndMargin=CfdRiskSizer.CalculateUnits(contract,account.Equity,p.StopDistancePoints,pointValue,
                live,conversion,_settings.MaxRiskPerTrade);
            var units=Math.Min(p.Units,byRiskAndMargin);
            if (units<=0 || units<p.Units*0.95m) return new(false,"Account equity, margin or currency conversion changed; proposal needs recalculation.");
            var dir=p.Side==CfdSide.Buy?1m:-1m;
            var distance=p.StopDistancePoints;
            var order=p with { Units=units,Entry=live,StopLoss=live-dir*distance,
                TakeProfit=live+dir*distance*CfdRiskLimits.RewardToRisk,
                MaxLoss=Math.Round(CfdRiskSizer.MaxLoss(units,p.StopDistancePoints,pointValue,conversion),2),PricePrecision=contract.PricePrecision };
            if (!_settings.TradingHours.IsOpen(_time.GetUtcNow().UtcDateTime)) return new(false,"CFD practice hours closed while checking the order.");
            var result=await _broker.PlacePracticeOrderAsync(order,ct);
            return new CfdDecisionResult(result.Filled,result.Message);
        }
        finally { _gate.Release(); }
    }

    private async Task<string?> NewsBlockAsync(string currency, DateTime nowUtc, CancellationToken ct)
    {
        var events=await _news.GetEventsAsync(ct);
        if (events is null) return "Economic calendar unavailable; CFD approval is paused.";
        var blocked=events.FirstOrDefault(e=>e.Currency.Equals(currency,StringComparison.OrdinalIgnoreCase) &&
            e.Impact.Equals("High",StringComparison.OrdinalIgnoreCase) &&
            (nowUtc-e.TimeUtc).Duration()<=TimeSpan.FromMinutes(30));
        return blocked is null ? null : $"News blackout: {blocked.Currency} {blocked.Title} at {blocked.TimeUtc:HH:mm} UTC.";
    }
}
