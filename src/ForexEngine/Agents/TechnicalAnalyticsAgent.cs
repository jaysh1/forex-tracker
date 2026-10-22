namespace ForexHitl;

/// <summary>
/// Technical Analytics Agent: polls completed 5m/15m candles, evaluates the strategy once per new
/// 5m candle, and hands signals to the Execution agent. Also drives guard refresh and proposal expiry.
/// </summary>
public sealed class TechnicalAnalyticsAgent : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly IBrokerClient _broker;
    private readonly ExecutionAgent _execution;
    private readonly AppConfig _config;
    private readonly ILogger<TechnicalAnalyticsAgent> _log;
    private readonly ActivityLog _activity;
    private readonly TradingWindow _window;
    private readonly TimeProvider _time;
    private readonly ScanStatusStore? _scanStatus;
    private readonly Dictionary<string, DateTime> _lastCandle = new();
    private readonly Dictionary<string, (string Explanation, DateTime LoggedUtc)> _lastActivityCheck = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastError;
    private DateTime _lastErrorAtUtc = DateTime.MinValue;
    private bool? _wasInHours;
    private static readonly TimeSpan RepeatCheckAfter = TimeSpan.FromMinutes(5);

    public TechnicalAnalyticsAgent(
        IBrokerClient broker, ExecutionAgent execution, AppConfig config, ActivityLog activity,
        TradingWindow window, TimeProvider time, ILogger<TechnicalAnalyticsAgent> log,
        ScanStatusStore? scanStatus = null)
    {
        _broker = broker;
        _execution = execution;
        _config = config;
        _activity = activity;
        _window = window;
        _time = time;
        _log = log;
        _scanStatus = scanStatus;
    }

    /// <summary>
    /// The full cause chain. .NET's top-level message for a network failure is just "An error occurred while
    /// sending the request."; the useful part (DNS, TLS, connection reset, timeout...) is in the inner exceptions.
    /// </summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message)) parts.Add(e.Message);
        return string.Join(" -> ", parts);
    }

    /// <summary>
    /// Shows scanner failures (wrong OANDA token, broker outage...) on the approval page, where the owner
    /// will see them, instead of only in server logs. The same message is repeated at most every 10 minutes.
    /// </summary>
    private void ReportFailure(string message)
    {
        var now = DateTime.UtcNow;
        if (message == _lastError && now - _lastErrorAtUtc < TimeSpan.FromMinutes(10)) return;

        _lastError = message;
        _lastErrorAtUtc = now;
        _activity.Add("error", $"Scanner problem: {(message.Length > 300 ? message[..300] + "…" : message)}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Scanner started for {Instruments} ({Env})",
            string.Join(", ", _config.Instruments), _config.UseLive ? "LIVE" : "practice");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scanner tick failed");
                ReportFailure(Describe(ex));
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Records when trading hours start and end. When they start, forget the previously seen candles so the
    /// first tick only records the latest candle and signals are judged on fresh data, not on what happened
    /// before the window opened.
    /// </summary>
    private void NoteTradingHours(bool inHours)
    {
        if (_wasInHours == inHours) return;
        _wasInHours = inHours;

        if (inHours) _lastCandle.Clear();
        _activity.Add("hours", inHours
            ? $"Trading hours started ({_window.Describe()}) - scanning for signals."
            : $"Outside trading hours ({_window.Describe()}) - not scanning for signals. Open trades keep their stop loss and take profit.");
    }

    private void RecordInstrumentCheck(Quote quote, string explanation, DateTime nowUtc)
    {
        if (_lastActivityCheck.TryGetValue(quote.Instrument, out var previous) &&
            previous.Explanation == explanation && nowUtc - previous.LoggedUtc < RepeatCheckAfter)
            return;

        _lastActivityCheck[quote.Instrument] = (explanation, nowUtc);
        var name = quote.Instrument.Replace('_', '/');
        var tradeability = quote.Tradeable ? "tradeable" : "not tradeable";
        var bid = quote.Bid > 0 ? quote.Bid.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
        var ask = quote.Ask > 0 ? quote.Ask.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
        _activity.Add("scan", $"{name} check: bid {bid}, ask {ask} ({tradeability}). {explanation}");
    }

    private void SetInstrumentExplanation(Quote quote, string explanation, DateTime nowUtc)
    {
        _scanStatus?.SetExplanation(quote.Instrument, explanation, nowUtc);
        RecordInstrumentCheck(quote, explanation, nowUtc);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        _execution.ExpireStale();

        // Always refresh the account figures and risk caps, so the page stays current outside trading hours.
        var status = await _execution.RefreshGuardAsync(ct);

        var inHours = _window.IsOpen(_time.GetUtcNow().UtcDateTime);
        NoteTradingHours(inHours);
        foreach (var instrument in _config.Instruments)
        {
            try
            {
                var quote = await _broker.GetQuoteAsync(instrument, ct);
                var now = _time.GetUtcNow().UtcDateTime;
                _scanStatus?.SetPrice(quote);

                if (!inHours)
                {
                    SetInstrumentExplanation(quote,
                        $"Not checking for entries: trading hours are closed ({_window.Describe()}).", now);
                    continue;
                }
                if (!status.CanTrade)
                {
                    SetInstrumentExplanation(quote,
                        $"Not checking for entries: {status.Reason ?? "trading is paused by the risk guard."}", now);
                    continue;
                }
                if (!quote.Tradeable)
                {
                    SetInstrumentExplanation(quote, "OANDA reports this market is not tradeable right now.", now);
                    continue;
                }

                var m5 = await _broker.GetCandlesAsync(instrument, "M5", 150, ct);
                if (m5.Count == 0)
                {
                    SetInstrumentExplanation(quote, "OANDA returned no completed 5-minute candles.", now);
                    continue;
                }

                var latest = m5[^1].TimeUtc;
                var seen = _lastCandle.TryGetValue(instrument, out var prev);
                _lastCandle[instrument] = latest;
                if (!seen)
                {
                    SetInstrumentExplanation(quote,
                        "Price is updating. Waiting for the next completed 5-minute candle before evaluating a new entry.", now);
                    continue;
                }
                if (latest <= prev) continue; // The stored setup explanation remains current until the next completed candle.

                var m15 = await _broker.GetCandlesAsync(instrument, "M15", 500, ct);
                var evaluation = SignalEngine.EvaluateWithReason(m5, m15);
                var explanation = evaluation.Signal is { } signal
                    ? await _execution.ProposeAsync(instrument, signal, ct)
                    : evaluation.Explanation;
                SetInstrumentExplanation(quote, explanation, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Scan failed for {Instrument}", instrument);
                var message = Describe(ex);
                var explanation = $"Could not complete this pair's check: {message}";
                _scanStatus?.SetExplanation(instrument, explanation, _time.GetUtcNow().UtcDateTime);
                ReportFailure($"{instrument}: {message}");
            }
        }
    }
}
