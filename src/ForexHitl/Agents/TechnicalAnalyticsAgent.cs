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
    private readonly Dictionary<string, DateTime> _lastCandle = new();
    private string? _lastError;
    private DateTime _lastErrorAtUtc = DateTime.MinValue;
    private bool? _wasInHours;

    public TechnicalAnalyticsAgent(
        IBrokerClient broker, ExecutionAgent execution, AppConfig config, ActivityLog activity,
        TradingWindow window, TimeProvider time, ILogger<TechnicalAnalyticsAgent> log)
    {
        _broker = broker;
        _execution = execution;
        _config = config;
        _activity = activity;
        _window = window;
        _time = time;
        _log = log;
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

    public async Task TickAsync(CancellationToken ct)
    {
        _execution.ExpireStale();

        // Always refresh the account figures and risk caps, so the page stays current outside trading hours.
        var status = await _execution.RefreshGuardAsync(ct);

        var inHours = _window.IsOpen(_time.GetUtcNow().UtcDateTime);
        NoteTradingHours(inHours);
        if (!inHours || !status.CanTrade) return;

        foreach (var instrument in _config.Instruments)
        {
            try
            {
                var m5 = await _broker.GetCandlesAsync(instrument, "M5", 150, ct);
                if (m5.Count == 0) continue;

                var latest = m5[^1].TimeUtc;
                var seen = _lastCandle.TryGetValue(instrument, out var prev);
                _lastCandle[instrument] = latest;
                if (!seen || latest <= prev) continue; // first sighting (possibly stale) or no new candle

                var m15 = await _broker.GetCandlesAsync(instrument, "M15", 500, ct);
                if (SignalEngine.Evaluate(m5, m15) is { } signal)
                    await _execution.ProposeAsync(instrument, signal, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Scan failed for {Instrument}", instrument);
                ReportFailure($"{instrument}: {Describe(ex)}");
            }
        }
    }
}
