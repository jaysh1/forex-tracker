namespace ForexHitl;

/// <summary>Indicator series are aligned to the input; entries before enough data exists are null.</summary>
public static class Indicators
{
    /// <summary>EMA seeded with the SMA of the first <paramref name="period"/> values.</summary>
    public static decimal?[] Ema(IReadOnlyList<decimal> values, int period)
    {
        var result = new decimal?[values.Count];
        if (period < 1 || values.Count < period) return result;

        decimal sum = 0;
        for (var i = 0; i < period; i++) sum += values[i];
        var ema = sum / period;
        result[period - 1] = ema;

        var k = 2m / (period + 1);
        for (var i = period; i < values.Count; i++)
        {
            ema = (values[i] - ema) * k + ema;
            result[i] = ema;
        }
        return result;
    }

    /// <summary>Wilder's RSI.</summary>
    public static decimal?[] Rsi(IReadOnlyList<decimal> closes, int period = 14)
    {
        var result = new decimal?[closes.Count];
        if (period < 1 || closes.Count <= period) return result;

        decimal gain = 0, loss = 0;
        for (var i = 1; i <= period; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change > 0) gain += change; else loss -= change;
        }

        var avgGain = gain / period;
        var avgLoss = loss / period;
        result[period] = ToRsi(avgGain, avgLoss);

        for (var i = period + 1; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            var g = change > 0 ? change : 0m;
            var l = change < 0 ? -change : 0m;
            avgGain = (avgGain * (period - 1) + g) / period;
            avgLoss = (avgLoss * (period - 1) + l) / period;
            result[i] = ToRsi(avgGain, avgLoss);
        }
        return result;
    }

    /// <summary>Wilder's Average True Range.</summary>
    public static decimal?[] Atr(IReadOnlyList<Candle> candles, int period = 14)
    {
        var result = new decimal?[candles.Count];
        if (period < 1 || candles.Count <= period) return result;

        var tr = new decimal[candles.Count];
        for (var i = 1; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            tr[i] = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
        }

        decimal sum = 0;
        for (var i = 1; i <= period; i++) sum += tr[i];
        var atr = sum / period;
        result[period] = atr;

        for (var i = period + 1; i < candles.Count; i++)
        {
            atr = (atr * (period - 1) + tr[i]) / period;
            result[i] = atr;
        }
        return result;
    }

    private static decimal ToRsi(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0) return avgGain == 0 ? 50m : 100m;
        return 100m - 100m / (1m + avgGain / avgLoss);
    }
}
