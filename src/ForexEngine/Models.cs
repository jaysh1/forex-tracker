namespace ForexHitl;

public enum Side { Buy, Sell }

public sealed record Candle(DateTime TimeUtc, decimal Open, decimal High, decimal Low, decimal Close);

public sealed record Quote(string Instrument, decimal Bid, decimal Ask, DateTime TimeUtc, bool Tradeable);

/// <param name="Currency">The account's home currency (e.g. "AUD"). Equity, P&amp;L and risk are all in this currency.</param>
public sealed record AccountSummary(decimal Nav, decimal Balance, string Currency);

public sealed record OrderResult(bool Success, string Message, string? TradeId);

/// <summary>Output of the Technical Analytics Agent. Contains no sizing - that is the Risk agent's job.</summary>
public sealed record Signal(Side Side, decimal Atr, string Rationale);

public sealed record TradeProposal(
    string Id,
    string Instrument,
    Side Side,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal StopLossPips,
    long Units,
    decimal RiskUsd,
    DateTime CreatedUtc,
    string Rationale);

public sealed record NewsEvent(DateTime TimeUtc, string Currency, string Title, string Impact)
{
    public bool IsHighImpact => string.Equals(Impact, "High", StringComparison.OrdinalIgnoreCase);
}

public sealed class BrokerException(string message) : Exception(message);
