using System.Globalization;
using System.Text.Json;

namespace ForexHitl;

public interface INewsCalendar
{
    /// <summary>Events, or null if the calendar cannot be obtained (callers must fail closed).</summary>
    Task<IReadOnlyList<NewsEvent>?> GetEventsAsync(CancellationToken ct);
}

public sealed record GateDecision(bool Allowed, string? Reason, Quote? Quote)
{
    public static GateDecision Block(string reason) => new(false, reason, null);
}

public static class NewsWindow
{
    /// <summary>First high-impact event for either currency within +/-30 minutes of now, if any.</summary>
    public static NewsEvent? FindBlockingEvent(IEnumerable<NewsEvent> events, string instrument, DateTime nowUtc)
    {
        var (b, q) = Instruments.Currencies(instrument);
        return events.FirstOrDefault(e =>
            e.IsHighImpact &&
            (e.Currency == b || e.Currency == q) &&
            (nowUtc - e.TimeUtc).Duration() <= RiskLimits.NewsBlackout);
    }
}

/// <summary>Volatility &amp; Macro Gatekeeper: spread cap and high-impact news blackout. Fails closed.</summary>
public sealed class GatekeeperAgent
{
    private readonly IBrokerClient _broker;
    private readonly INewsCalendar _calendar;
    private readonly TimeProvider _time;

    public GatekeeperAgent(IBrokerClient broker, INewsCalendar calendar, TimeProvider time)
    {
        _broker = broker;
        _calendar = calendar;
        _time = time;
    }

    public static decimal SpreadPips(Quote q, string instrument) => (q.Ask - q.Bid) / Instruments.PipSize(instrument);

    public async Task<GateDecision> CheckAsync(string instrument, CancellationToken ct)
    {
        var quote = await _broker.GetQuoteAsync(instrument, ct);
        if (!quote.Tradeable) return GateDecision.Block($"{instrument} is not tradeable right now (market closed?)");

        var spread = SpreadPips(quote, instrument);
        if (spread > RiskLimits.MaxSpreadPips)
            return GateDecision.Block(
                $"Spread {spread.ToString("F1", CultureInfo.InvariantCulture)} pips exceeds the {RiskLimits.MaxSpreadPips} pip cap");

        var events = await _calendar.GetEventsAsync(ct);
        if (events is null) return GateDecision.Block("Economic calendar unavailable - failing closed");

        var now = _time.GetUtcNow().UtcDateTime;
        if (NewsWindow.FindBlockingEvent(events, instrument, now) is { } ev)
            return GateDecision.Block($"News blackout: {ev.Currency} {ev.Title} at {ev.TimeUtc:HH:mm} UTC");

        return new GateDecision(true, null, quote);
    }
}

/// <summary>
/// Pulls a weekly calendar JSON feed (default: the unofficial Faireconomy / Forex Factory export).
/// Cached for an hour, backs off on failure, and serves stale data for at most 12h before reporting
/// "unavailable" so the gatekeeper fails closed. Swap this class for a paid API if you need reliability.
/// </summary>
public sealed class HttpNewsCalendar : INewsCalendar
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxStale = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _urls;
    private readonly TimeProvider _time;
    private readonly ILogger<HttpNewsCalendar> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IReadOnlyList<NewsEvent>? _cache;
    private DateTime _fetchedUtc;
    private DateTime _nextAttemptUtc;

    public HttpNewsCalendar(HttpClient http, AppConfig config, TimeProvider time, ILogger<HttpNewsCalendar> log)
    {
        _http = http;
        _urls = config.CalendarUrls;
        _time = time;
        _log = log;
    }

    public async Task<IReadOnlyList<NewsEvent>?> GetEventsAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (_cache is not null && now - _fetchedUtc < RefreshInterval) return _cache;

            if (now >= _nextAttemptUtc)
            {
                var events = new List<NewsEvent>();
                var anyOk = false;
                foreach (var url in _urls)
                {
                    try
                    {
                        events.AddRange(await FetchAsync(url, ct));
                        anyOk = true;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        _log.LogWarning("Calendar fetch failed for {Url}: {Message}", url, ex.Message);
                    }
                }

                if (anyOk)
                {
                    _cache = events;
                    _fetchedUtc = now;
                    return _cache;
                }
                _nextAttemptUtc = now + RetryBackoff;
            }

            return _cache is not null && now - _fetchedUtc < MaxStale ? _cache : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<NewsEvent>> FetchAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var list = new List<NewsEvent>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("date", out var d) ||
                !DateTimeOffset.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
                continue;

            string Str(string n) => e.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
            list.Add(new NewsEvent(when.UtcDateTime, Str("country").ToUpperInvariant(), Str("title"), Str("impact")));
        }
        return list;
    }
}
