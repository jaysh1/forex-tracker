namespace ForexHitl;

/// <summary>Turns a signal plus a live quote into a fully sized proposal. Pure, so it is unit-tested.</summary>
public static class ProposalBuilder
{
    public const decimal AtrStopMultiple = 1.5m;
    public const decimal MinStopPips = 8m;
    public const decimal MaxStopPips = 40m;

    public static TradeProposal? Build(string instrument, Signal signal, Quote quote, decimal equity, DateTime nowUtc,
        decimal usdToAccount = 1m, decimal maxRiskPerTrade = RiskManager.DefaultMaxRiskPerTrade)
    {
        var pip = Instruments.PipSize(instrument);
        var dir = signal.Side == Side.Buy ? 1m : -1m;
        var entry = signal.Side == Side.Buy ? quote.Ask : quote.Bid;

        var slDist = Math.Round(
            Math.Clamp(signal.Atr * AtrStopMultiple, MinStopPips * pip, MaxStopPips * pip), 5);
        var slPips = slDist / pip;

        var units = RiskManager.CalculateUnits(instrument, equity, slPips, entry, usdToAccount, maxRiskPerTrade);
        if (units < 1) return null;

        return new TradeProposal(
            Id: Guid.NewGuid().ToString("N")[..8],
            Instrument: instrument,
            Side: signal.Side,
            EntryPrice: entry,
            StopLoss: entry - dir * slDist,
            TakeProfit: entry + dir * slDist * RiskLimits.MinRewardToRisk,
            StopLossPips: slPips,
            Units: units,
            RiskUsd: Math.Round(RiskManager.RiskUsd(instrument, units, slPips, usdToAccount), 2),
            CreatedUtc: nowUtc,
            Rationale: signal.Rationale);
    }

    /// <summary>
    /// Re-anchors stop and target to the live price at approval time, keeping the same pip distances,
    /// so price drift while waiting for the human cannot change the dollar risk or the 1:2 ratio.
    /// </summary>
    public static TradeProposal Reanchor(TradeProposal p, decimal livePrice)
    {
        var dir = p.Side == Side.Buy ? 1m : -1m;
        var dist = p.StopLossPips * Instruments.PipSize(p.Instrument);
        return p with
        {
            EntryPrice = livePrice,
            StopLoss = livePrice - dir * dist,
            TakeProfit = livePrice + dir * dist * RiskLimits.MinRewardToRisk
        };
    }
}
