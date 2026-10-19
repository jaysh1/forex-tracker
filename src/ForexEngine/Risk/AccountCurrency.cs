namespace ForexHitl;

/// <summary>
/// Position sizing works in USD pip values (EUR_USD and GBP_USD are USD-quoted). Equity, and therefore the
/// 0.5% risk budget, is in the account's own currency. On an AUD account the two must be reconciled with the
/// live AUD/USD rate; otherwise real risk comes out about 50% too high.
/// </summary>
public static class AccountCurrency
{
    // OANDA quotes these as XXX_USD (the USD price of one XXX); every other currency as USD_XXX.
    private static readonly HashSet<string> QuotedAgainstUsd = new() { "AUD", "EUR", "GBP", "NZD" };

    public static string PairFor(string currency)
    {
        currency = currency.ToUpperInvariant();
        return QuotedAgainstUsd.Contains(currency) ? $"{currency}_USD" : $"USD_{currency}";
    }

    /// <summary>
    /// Account-currency units per 1 USD. Rounds in the cautious direction (the one that makes a USD loss
    /// look larger in account currency), so sizing errs small.
    /// </summary>
    public static decimal UsdToAccount(string currency, Quote quote)
    {
        if (quote.Bid <= 0 || quote.Ask <= 0)
            throw new BrokerException($"No usable {PairFor(currency)} price to convert risk into {currency}.");

        return QuotedAgainstUsd.Contains(currency.ToUpperInvariant()) ? 1m / quote.Bid : quote.Ask;
    }

    public static async Task<decimal> GetUsdToAccountAsync(IBrokerClient broker, string currency, CancellationToken ct)
    {
        if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase)) return 1m;
        var quote = await broker.GetQuoteAsync(PairFor(currency), ct);
        return UsdToAccount(currency, quote);
    }
}
