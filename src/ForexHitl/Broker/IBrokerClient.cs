namespace ForexHitl;

/// <summary>Broker abstraction. OANDA v20 is the shipped implementation; implement this to add another.</summary>
public interface IBrokerClient
{
    Task<AccountSummary> GetAccountAsync(CancellationToken ct);
    Task<Quote> GetQuoteAsync(string instrument, CancellationToken ct);

    /// <summary>Completed candles only, oldest first.</summary>
    Task<IReadOnlyList<Candle>> GetCandlesAsync(string instrument, string granularity, int count, CancellationToken ct);

    Task<bool> HasOpenPositionAsync(string instrument, CancellationToken ct);

    /// <summary>Market order (fill-or-kill) with stop loss and take profit attached on fill.</summary>
    Task<OrderResult> PlaceMarketOrderAsync(TradeProposal proposal, CancellationToken ct);
}
