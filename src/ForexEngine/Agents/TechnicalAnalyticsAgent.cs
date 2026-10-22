using System.Globalization;

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
    private readonly Dictionary<string, DateTime> _lastIndicatorCandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastIndicatorPollUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastScannedHistorySummary = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Explanation, DateTime LoggedUtc)> _lastActivityCheck = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastError;
    private DateTime _lastErrorAtUtc = DateTime.MinValue;
    private bool? _wasInHours;
    private static readonly TimeSpan RepeatCheckAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IndicatorPollInterval = TimeSpan.FromMinutes(1);

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

    private void RecordInstrumentCheck(Quote quote, string explanation, DateTime nowUtc, bool logEveryCheck = false)
    {
        if (!logEveryCheck && _lastActivityCheck.TryGetValue(quote.Instrument, out var previous) &&
            previous.Explanation == explanation && nowUtc - previous.LoggedUtc < RepeatCheckAfter)
            return;

        _lastActivityCheck[quote.Instrument] = (explanation, nowUtc);
        var name = quote.Instrument.Replace('_', '/');
        var tradeability = quote.Tradeable ? "tradeable" : "not tradeable";
        var bid = quote.Bid > 0 ? quote.Bid.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
        var ask = quote.Ask > 0 ? quote.Ask.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
        _activity.Add("scan", $"{name} check: bid {bid}, ask {ask} ({tradeability}). {explanation}");
    }

    private void SetInstrumentExplanation(Quote quote, string explanation, DateTime nowUtc, bool logEveryCheck = false)
    {
        _scanStatus?.SetExplanation(quote.Instrument, explanation, nowUtc);
        RecordInstrumentCheck(quote, explanation, nowUtc, logEveryCheck);
    }

    private static string ScannedHistorySummary(
        IReadOnlyList<Candle> m5, IReadOnlyList<Candle> m15, IndicatorSeriesSnapshot? indicators)
    {
        static string CandleLabel(Candle candle) =>
            $"{candle.TimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC close {candle.Close.ToString("F5", CultureInfo.InvariantCulture)}";

        var latestM5 = m5.Count > 0 ? CandleLabel(m5[^1]) : "unavailable";
        var latestM15 = m15.Count > 0 ? CandleLabel(m15[^1]) : "unavailable";
        if (indicators is null)
            return $"Scanned candle history: M5 {m5.Count} (latest {latestM5}), M15 {m15.Count} (latest {latestM15}). " +
                   $"EMA/RSI needs at least {SignalEngine.RsiPeriod + 2} completed M5 candles and {SignalEngine.SlowEma} completed M15 candles.";

        var ema50 = indicators.Ema50.LastOrDefault(value => value.HasValue);
        var ema200 = indicators.Ema200.LastOrDefault(value => value.HasValue);
        var previousRsi = indicators.Rsi.Length > 1 ? indicators.Rsi[^2] : null;
        var currentRsi = indicators.Rsi.LastOrDefault(value => value.HasValue);
        return $"Scanned candle history: M5 {m5.Count} (latest {latestM5}), M15 {m15.Count} (latest {latestM15}). " +
               $"EMA50 {ema50?.ToString("F5", CultureInfo.InvariantCulture) ?? "—"}; " +
               $"EMA200 {ema200?.ToString("F5", CultureInfo.InvariantCulture) ?? "—"}; " +
               $"RSI14 {previousRsi?.ToString("F1", CultureInfo.InvariantCulture) ?? "—"} → " +
               $"{currentRsi?.ToString("F1", CultureInfo.InvariantCulture) ?? "—"}.";
    }

    private void RecordUnchangedCandlePoll(Quote quote, IReadOnlyList<Candle> m5, DateTime nowUtc)
    {
        var latest = m5[^1].TimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        var previous = _scanStatus?.Snapshot().FirstOrDefault(item =>
            item.Instrument.Equals(quote.Instrument, StringComparison.OrdinalIgnoreCase))?.Explanation;
        var bid = quote.Bid.ToString("F5", CultureInfo.InvariantCulture);
        var ask = quote.Ask.ToString("F5", CultureInfo.InvariantCulture);
        var name = quote.Instrument.Replace('_', '/');
        _activity.Add("scan", $"{name} poll: bid {bid}, ask {ask}; latest completed M5 candle {latest} is unchanged, so no new entry evaluation. Last scan: {previous ?? "waiting for indicator history"}");
        _lastActivityCheck[quote.Instrument] = (previous ?? "", nowUtc);
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

                var canScanForEntries = inHours && status.CanTrade && quote.Tradeable;
                if (!canScanForEntries && _lastIndicatorPollUtc.TryGetValue(instrument, out var lastIndicatorPoll) &&
                    now - lastIndicatorPoll < IndicatorPollInterval)
                    continue;
                if (!canScanForEntries) _lastIndicatorPollUtc[instrument] = now;

                var m5 = await _broker.GetCandlesAsync(instrument, "M5", 150, ct);
                if (m5.Count == 0)
                {
                    var candleExplanation = canScanForEntries
                        ? "OANDA returned no completed 5-minute candles."
                        : $"{EntryScanPauseReason(inHours, status.CanTrade, quote.Tradeable, status.Reason)} No completed 5-minute candles are available for indicator charts.";
                    SetInstrumentExplanation(quote, candleExplanation, now, logEveryCheck: true);
                    continue;
                }

                var latest = m5[^1].TimeUtc;
                if (!canScanForEntries)
                {
                    string historySummary;
                    if (!_lastIndicatorCandle.TryGetValue(instrument, out var previousIndicatorCandle) || latest > previousIndicatorCandle)
                    {
                        var displayM15 = await _broker.GetCandlesAsync(instrument, "M15", 500, ct);
                        var displayIndicators = SignalEngine.BuildIndicatorSeries(m5, displayM15);
                        _lastIndicatorCandle[instrument] = latest;
                        historySummary = ScannedHistorySummary(m5, displayM15, displayIndicators);
                        _lastScannedHistorySummary[instrument] = historySummary;
                        if (displayIndicators is not null) _scanStatus?.SetIndicators(instrument, displayIndicators, now);
                    }
                    else historySummary = _lastScannedHistorySummary.GetValueOrDefault(instrument) ??
                        $"No new M5 candle; latest completed candle is {latest.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.";

                    SetInstrumentExplanation(quote,
                        $"{historySummary} {EntryScanPauseReason(inHours, status.CanTrade, quote.Tradeable, status.Reason)}",
                        now, logEveryCheck: true);
                    continue;
                }

                var seen = _lastCandle.TryGetValue(instrument, out var prev);
                _lastCandle[instrument] = latest;
                if (seen && latest <= prev)
                {
                    RecordUnchangedCandlePoll(quote, m5, now);
                    continue; // The chart and setup remain current until the next completed candle.
                }

                var m15 = await _broker.GetCandlesAsync(instrument, "M15", 500, ct);
                var indicators = SignalEngine.BuildIndicatorSeries(m5, m15);
                _lastIndicatorCandle[instrument] = latest;
                var scanSummary = ScannedHistorySummary(m5, m15, indicators);
                _lastScannedHistorySummary[instrument] = scanSummary;
                if (indicators is not null) _scanStatus?.SetIndicators(instrument, indicators, now);
                if (!seen)
                {
                    SetInstrumentExplanation(quote,
                        $"{scanSummary} Historical chart data loaded. Waiting for the next completed 5-minute candle before evaluating an entry.",
                        now, logEveryCheck: true);
                    continue;
                }

                var evaluation = SignalEngine.EvaluateWithReason(m5, m15);
                var explanation = evaluation.Signal is { } signal
                    ? await _execution.ProposeAsync(instrument, signal, ct)
                    : evaluation.Explanation;
                SetInstrumentExplanation(quote, $"{scanSummary} {explanation}", now, logEveryCheck: true);
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

    private string EntryScanPauseReason(bool inHours, bool canTrade, bool tradeable, string? reason)
    {
        if (!inHours) return $"Not checking for entries: trading hours are closed ({_window.Describe()}).";
        if (!canTrade) return $"Not checking for entries: {reason ?? "trading is paused by the risk guard."}";
        if (!tradeable) return "Not checking for entries: OANDA reports this market is not tradeable right now.";
        return "Not checking for entries.";
    }
}
