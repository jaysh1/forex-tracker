namespace StockEngine;

public enum StockSide { Buy, Sell }
public sealed record StockBar(DateTime TimeUtc, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
public sealed record StockQuote(string Symbol, decimal Bid, decimal Ask, DateTime TimeUtc, bool MarketOpen);
public sealed record StockAccount(decimal Equity, decimal Cash, string Currency);
public sealed record StockSignal(StockSide Side, string Rationale);
public sealed record StockOrder(string Symbol, StockSide Side, decimal Shares, decimal LimitPrice, string Currency);
public sealed record StockOrderResult(bool Filled, string Message, string? OrderId);

/// <summary>Stock market access is independent; providers must implement the stock-specific contract.</summary>
public interface IStockBroker
{
    Task<StockAccount> GetAccountAsync(CancellationToken cancellationToken);
    Task<StockQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<StockBar>> GetBarsAsync(string symbol, string interval, int count, CancellationToken cancellationToken);
    Task<StockOrderResult> SubmitPaperOrderAsync(StockOrder order, CancellationToken cancellationToken);
}

public interface IStockStrategy
{
    string Name { get; }
    StockSignal? Evaluate(IReadOnlyList<StockBar> bars);
}

public sealed record StockRiskPolicy(decimal MaxRiskPerTrade, decimal MaxPositionValue)
{
    public decimal Size(decimal equity, decimal entryPrice, decimal stopPrice)
    {
        if (MaxRiskPerTrade <= 0 || MaxPositionValue <= 0 || equity <= 0 || entryPrice <= 0 || stopPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(entryPrice), "Stock sizing inputs must be positive.");
        var riskPerShare = Math.Abs(entryPrice - stopPrice);
        if (riskPerShare == 0) return 0;
        return decimal.Floor(Math.Min(MaxRiskPerTrade / riskPerShare, Math.Min(MaxPositionValue, equity) / entryPrice));
    }
}

public sealed record StockEngineOptions(IReadOnlyList<string> Symbols, string Currency, bool PaperOnly = true)
{
    public bool Enabled => Symbols.Count > 0;
}
