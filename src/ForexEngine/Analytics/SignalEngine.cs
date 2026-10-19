namespace ForexHitl;

/// <summary>
/// Pure strategy logic (no I/O) so it is unit-testable.
/// Trend filter: 15m EMA50 vs EMA200. Trigger: 5m RSI(14) leaving oversold (long, uptrend) or
/// overbought (short, downtrend). Expects COMPLETED candles only.
/// This is a plain, textbook rule set - it is a starting point, not a demonstrated edge.
/// </summary>
public static class SignalEngine
{
    public const int FastEma = 50;
    public const int SlowEma = 200;
    public const int RsiPeriod = 14;
    public const int AtrPeriod = 14;
    public const decimal Oversold = 30m;
    public const decimal Overbought = 70m;

    public static Signal? Evaluate(IReadOnlyList<Candle> m5, IReadOnlyList<Candle> m15)
    {
        if (m15.Count < SlowEma || m5.Count < Math.Max(RsiPeriod, AtrPeriod) + 2) return null;

        var closes15 = m15.Select(c => c.Close).ToList();
        var ema50 = Indicators.Ema(closes15, FastEma)[^1];
        var ema200 = Indicators.Ema(closes15, SlowEma)[^1];
        if (ema50 is not { } fast || ema200 is not { } slow) return null;

        var rsi = Indicators.Rsi(m5.Select(c => c.Close).ToList(), RsiPeriod);
        var atr = Indicators.Atr(m5, AtrPeriod)[^1];
        if (rsi[^2] is not { } prev || rsi[^1] is not { } cur || atr is not { } a || a <= 0) return null;

        if (fast > slow && prev < Oversold && cur >= Oversold)
            return new Signal(Side.Buy, a,
                $"15m uptrend (EMA50 {fast:F5} > EMA200 {slow:F5}); 5m RSI {prev:F1} -> {cur:F1} leaving oversold");

        if (fast < slow && prev > Overbought && cur <= Overbought)
            return new Signal(Side.Sell, a,
                $"15m downtrend (EMA50 {fast:F5} < EMA200 {slow:F5}); 5m RSI {prev:F1} -> {cur:F1} leaving overbought");

        return null;
    }
}
