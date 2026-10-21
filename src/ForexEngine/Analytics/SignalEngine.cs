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

    public static Signal? Evaluate(IReadOnlyList<Candle> m5, IReadOnlyList<Candle> m15) =>
        EvaluateWithReason(m5, m15).Signal;

    /// <summary>Evaluates the setup and explains the first condition that currently prevents an entry.</summary>
    public static SignalEvaluation EvaluateWithReason(IReadOnlyList<Candle> m5, IReadOnlyList<Candle> m15)
    {
        if (m15.Count < SlowEma)
            return new(null, $"Waiting for more 15-minute history ({m15.Count}/{SlowEma} candles needed for the trend filter).");
        if (m5.Count < Math.Max(RsiPeriod, AtrPeriod) + 2)
            return new(null, $"Waiting for more 5-minute history ({m5.Count}/{Math.Max(RsiPeriod, AtrPeriod) + 2} candles needed).");

        var closes15 = m15.Select(c => c.Close).ToList();
        var ema50 = Indicators.Ema(closes15, FastEma)[^1];
        var ema200 = Indicators.Ema(closes15, SlowEma)[^1];
        if (ema50 is not { } fast || ema200 is not { } slow)
            return new(null, "The 15-minute trend indicators are not ready yet.");

        var rsi = Indicators.Rsi(m5.Select(c => c.Close).ToList(), RsiPeriod);
        var atr = Indicators.Atr(m5, AtrPeriod)[^1];
        if (rsi[^2] is not { } prev || rsi[^1] is not { } cur || atr is not { } a || a <= 0)
            return new(null, "The 5-minute RSI or ATR indicator is not ready yet.");

        if (fast > slow && prev < Oversold && cur >= Oversold)
        {
            var signal = new Signal(Side.Buy, a,
                $"15m uptrend (EMA50 {fast:F5} > EMA200 {slow:F5}); 5m RSI {prev:F1} -> {cur:F1} leaving oversold");
            return new(signal, signal.Rationale);
        }

        if (fast < slow && prev > Overbought && cur <= Overbought)
        {
            var signal = new Signal(Side.Sell, a,
                $"15m downtrend (EMA50 {fast:F5} < EMA200 {slow:F5}); 5m RSI {prev:F1} -> {cur:F1} leaving overbought");
            return new(signal, signal.Rationale);
        }

        if (fast == slow)
            return new(null, $"No trade: 15-minute trend is flat (EMA50 {fast:F5}, EMA200 {slow:F5}); RSI is {prev:F1} -> {cur:F1}.");

        if (fast > slow)
        {
            var rsiReason = cur < Oversold
                ? $"5-minute RSI remains oversold ({prev:F1} -> {cur:F1}); waiting for it to cross back above {Oversold:F0}."
                : $"5-minute RSI did not cross up from below {Oversold:F0} ({prev:F1} -> {cur:F1}).";
            return new(null, $"No trade: 15-minute trend is up (EMA50 {fast:F5} > EMA200 {slow:F5}), but {rsiReason}");
        }

        var downReason = cur > Overbought
            ? $"5-minute RSI remains overbought ({prev:F1} -> {cur:F1}); waiting for it to cross back below {Overbought:F0}."
            : $"5-minute RSI did not cross down from above {Overbought:F0} ({prev:F1} -> {cur:F1}).";
        return new(null, $"No trade: 15-minute trend is down (EMA50 {fast:F5} < EMA200 {slow:F5}), but {downReason}");
    }
}

public sealed record SignalEvaluation(Signal? Signal, string Explanation);
