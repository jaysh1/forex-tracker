namespace ForexHitl;

/// <summary>
/// Only USD-quoted majors are supported so that pip value in USD is simply the pip size per unit.
/// Adding a JPY or cross pair needs a conversion-rate lookup - deliberately not supported here.
/// </summary>
public static class Instruments
{
    public static decimal PipSize(string instrument) => instrument switch
    {
        "EUR_USD" or "GBP_USD" => 0.0001m,
        _ => throw new NotSupportedException(
            $"Instrument '{instrument}' is not supported (USD-quoted majors EUR_USD / GBP_USD only).")
    };

    /// <summary>USD value of one pip for one unit of a USD-quoted pair.</summary>
    public static decimal PipValueUsdPerUnit(string instrument) => PipSize(instrument);

    public static (string Base, string Quote) Currencies(string instrument)
    {
        var parts = instrument.Split('_');
        if (parts.Length != 2) throw new ArgumentException($"Bad instrument '{instrument}'.", nameof(instrument));
        return (parts[0], parts[1]);
    }
}
