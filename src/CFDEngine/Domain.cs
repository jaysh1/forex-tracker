namespace CFDEngine;

public enum CfdSide { Buy, Sell }

public sealed record CfdCandle(DateTime TimeUtc, decimal Open, decimal High, decimal Low, decimal Close);
public sealed record CfdQuote(string Contract, decimal Bid, decimal Ask, DateTime TimeUtc, bool Tradeable);
public sealed record CfdAccount(decimal Equity, decimal Balance, string Currency);
public sealed record CfdSignal(CfdSide Side, decimal Atr, string Rationale);
public sealed record CfdOrderResult(bool Filled, string Message, string? TradeId);

public sealed record CfdContract(
    string Name, int PricePrecision, int UnitsPrecision, decimal PipSize,
    decimal MinimumUnits, decimal MarginRate, string QuoteCurrency, decimal MaximumUnits = long.MaxValue);

/// <summary>All CFD market access is supplied by the CFD engine's broker adapter.</summary>
public interface ICfdBroker
{
    Task<CfdAccount> GetAccountAsync(CancellationToken cancellationToken);
    Task<CfdContract> GetContractAsync(string symbol, CancellationToken cancellationToken);
    Task<CfdQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<CfdCandle>> GetCandlesAsync(string symbol, string interval, int count, CancellationToken cancellationToken);
    Task<bool> HasOpenPositionAsync(string symbol, CancellationToken cancellationToken);
    Task<CfdOrderResult> PlacePracticeOrderAsync(CfdTradeProposal request, CancellationToken cancellationToken);
}

/// <summary>Strategy and sizing contracts belong to CFDs and can evolve independently of Forex.</summary>
public interface ICfdStrategy
{
    string Name { get; }
    CfdSignal? Evaluate(IReadOnlyList<CfdCandle> shortInterval, IReadOnlyList<CfdCandle> longInterval);
}

public sealed record CfdRiskPolicy(decimal MaxRiskPerTrade, decimal MaxLeverage = 10m,
    decimal EquityRiskPercent = 0.5m, decimal RewardToRisk = 2m)
{
    public long Size(CfdContract contract, decimal equity, decimal stopDistance, decimal entry, decimal quoteToAccount)
    {
        if (MaxRiskPerTrade <= 0 || equity <= 0 || stopDistance <= 0 || entry <= 0 || quoteToAccount <= 0 ||
            contract.PipSize <= 0 || contract.MarginRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(stopDistance), "CFD sizing inputs must be positive.");

        var riskBudget = Math.Min(MaxRiskPerTrade, equity * EquityRiskPercent / 100m);
        var lossPerUnit = stopDistance * contract.PipSize * quoteToAccount;
        var byRisk = decimal.Floor(riskBudget / lossPerUnit);
        var leverage = Math.Min(MaxLeverage, 1m / contract.MarginRate);
        var byMargin = decimal.Floor(equity * leverage / (entry * quoteToAccount));
        var units = Math.Min(Math.Min(byRisk, byMargin), contract.MaximumUnits);
        return units < contract.MinimumUnits || units > long.MaxValue ? 0 : (long)units;
    }
}

public sealed record CfdEngineOptions(IReadOnlyList<string> Symbols, decimal MaxRiskPerTrade, bool PracticeOnly = true)
{
    public bool Enabled => Symbols.Count > 0;
}
