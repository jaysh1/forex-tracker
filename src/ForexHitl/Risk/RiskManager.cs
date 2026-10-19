namespace ForexHitl;

public static class RiskManager
{
    public const decimal PipValuePerStandardLot = 10.00m; // USD, EURUSD / GBPUSD
    public const decimal UnitsPerStandardLot = 100_000m;

    /// <summary>Dollar risk allowed on one trade: 0.5% of current equity.</summary>
    public static decimal AllowedDollarRisk(decimal equity) =>
        equity * (RiskLimits.RiskPerTradePct / 100m);

    /// <summary>
    /// Lot size (2dp) for the given equity and stop distance. Rounds DOWN: the original blueprint's
    /// Math.Round could round up and push risk above 0.5% (e.g. a 3 pip stop gives 1.67 lots = $50.10).
    /// </summary>
    public static decimal CalculateLotSize(decimal equity, decimal stopLossPips)
    {
        if (equity <= 0) throw new ArgumentException("Equity must be greater than zero.", nameof(equity));
        if (stopLossPips <= 0) throw new ArgumentException("Stop Loss pips must be greater than zero.", nameof(stopLossPips));

        var lots = AllowedDollarRisk(equity) / (stopLossPips * PipValuePerStandardLot);
        return Math.Floor(lots * 100m) / 100m;
    }

    /// <summary>
    /// Whole broker units (OANDA trades in units, 100,000 = 1 standard lot), rounded down, and capped
    /// by <see cref="RiskLimits.MaxLeverage"/>. Returns 0 if the trade cannot be sized safely.
    /// </summary>
    /// <param name="usdToAccount">Account-currency units per 1 USD (1 for a USD account). Pip values here are USD, so
    /// on e.g. an AUD account they must be converted or the real risk is wrong.</param>
    public static long CalculateUnits(string instrument, decimal equity, decimal stopLossPips, decimal entryPrice,
        decimal usdToAccount = 1m)
    {
        if (usdToAccount <= 0) throw new ArgumentException("Conversion rate must be greater than zero.", nameof(usdToAccount));
        if (equity <= 0) throw new ArgumentException("Equity must be greater than zero.", nameof(equity));
        if (stopLossPips <= 0) throw new ArgumentException("Stop Loss pips must be greater than zero.", nameof(stopLossPips));
        if (entryPrice <= 0) throw new ArgumentException("Entry price must be greater than zero.", nameof(entryPrice));

        var pipValuePerUnit = Instruments.PipValueUsdPerUnit(instrument) * usdToAccount;
        var byRisk = Math.Floor(AllowedDollarRisk(equity) / (stopLossPips * pipValuePerUnit));
        var byLeverage = Math.Floor(equity * RiskLimits.MaxLeverage / (entryPrice * usdToAccount));
        return (long)Math.Min(byRisk, byLeverage);
    }

    /// <summary>Loss if the stop is hit, in ACCOUNT currency (the name is historical; it is USD only for a USD account).</summary>
    public static decimal RiskUsd(string instrument, long units, decimal stopLossPips, decimal usdToAccount = 1m) =>
        units * stopLossPips * Instruments.PipValueUsdPerUnit(instrument) * usdToAccount;
}
