namespace ForexHitl;

/// <summary>Latest quote and entry-check explanation for each Forex instrument.</summary>
public sealed record InstrumentScanDto(
    string Instrument, decimal? Bid, decimal? Ask, bool? Tradeable,
    DateTime? PriceTimeUtc, DateTime? ReasonUpdatedUtc, string Explanation,
    InstrumentIndicatorSnapshot? Indicators = null);

public sealed record InstrumentIndicatorSnapshot(IndicatorSeriesSnapshot Series, DateTime UpdatedUtc);

/// <summary>Thread-safe, in-memory snapshot shared by the Forex scanner and authenticated dashboard API.</summary>
public sealed class ScanStatusStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InstrumentScanDto> _items = new(StringComparer.OrdinalIgnoreCase);

    public void SetPrice(Quote quote)
    {
        lock (_gate)
        {
            _items.TryGetValue(quote.Instrument, out var current);
            var explanation = current?.Explanation ?? "Waiting for the first completed 5-minute candle check.";
            _items[quote.Instrument] = new InstrumentScanDto(
                quote.Instrument, quote.Bid > 0 ? quote.Bid : null, quote.Ask > 0 ? quote.Ask : null,
                quote.Tradeable, quote.TimeUtc, current?.ReasonUpdatedUtc, explanation, current?.Indicators);
        }
    }

    public void SetExplanation(string instrument, string explanation, DateTime evaluatedUtc)
    {
        lock (_gate)
        {
            _items.TryGetValue(instrument, out var current);
            _items[instrument] = new InstrumentScanDto(
                instrument, current?.Bid, current?.Ask, current?.Tradeable, current?.PriceTimeUtc,
                evaluatedUtc, explanation, current?.Indicators);
        }
    }

    public void SetIndicators(string instrument, IndicatorSeriesSnapshot indicators, DateTime updatedUtc)
    {
        lock (_gate)
        {
            _items.TryGetValue(instrument, out var current);
            _items[instrument] = new InstrumentScanDto(
                instrument, current?.Bid, current?.Ask, current?.Tradeable, current?.PriceTimeUtc,
                current?.ReasonUpdatedUtc, current?.Explanation ?? "Waiting for the first completed 5-minute candle check.",
                new InstrumentIndicatorSnapshot(indicators, updatedUtc));
        }
    }

    public IReadOnlyList<InstrumentScanDto> Snapshot()
    {
        lock (_gate) return _items.Values.OrderBy(x => x.Instrument, StringComparer.Ordinal).ToArray();
    }
}
