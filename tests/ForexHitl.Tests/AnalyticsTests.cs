using ForexHitl;

namespace ForexHitl.Tests;

public class IndicatorTests
{
    [Fact]
    public void Ema_OfConstantSeries_IsConstant()
    {
        var ema = Indicators.Ema(Enumerable.Repeat(5m, 30).ToList(), 10);
        Assert.Null(ema[8]);
        Assert.Equal(5m, ema[9]);
        Assert.Equal(5m, ema[^1]);
    }

    [Fact]
    public void Ema_SeedsWithSma_ThenSmooths()
    {
        var ema = Indicators.Ema(new List<decimal> { 1, 2, 3, 4, 5 }, 3);
        Assert.Equal(2m, ema[2]);   // SMA(1,2,3)
        Assert.Equal(3m, ema[3]);   // (4-2)*0.5 + 2
        Assert.Equal(4m, ema[4]);
    }

    [Fact]
    public void Rsi_RisingIs100_FallingIs0()
    {
        var up = Indicators.Rsi(Enumerable.Range(1, 40).Select(i => (decimal)i).ToList());
        var down = Indicators.Rsi(Enumerable.Range(1, 40).Select(i => (decimal)(100 - i)).ToList());
        Assert.Equal(100m, up[^1]);
        Assert.Equal(0m, down[^1]);
    }

    [Fact]
    public void Atr_OfUniformCandles_EqualsTheirRange()
    {
        var candles = Enumerable.Range(0, 30)
            .Select(i => new Candle(DateTime.UnixEpoch.AddMinutes(5 * i), 10m, 10.5m, 9.5m, 10m))
            .ToList();
        Assert.Equal(1m, Indicators.Atr(candles, 14)[^1]);
    }
}

public class SignalEngineTests
{
    private static List<Candle> Make(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new Candle(DateTime.UnixEpoch.AddMinutes(5 * i), c, c + 0.0001m, c - 0.0001m, c)).ToList();

    private static List<Candle> Trend(decimal start, decimal step, int n) =>
        Make(Enumerable.Range(0, n).Select(i => start + step * i));

    private static List<Candle> M5Fall(decimal finalMove) =>
        Make(Enumerable.Range(0, 60).Select(i => 1.1000m - 0.0005m * i).Append(1.1000m - 0.0005m * 59 + finalMove));

    private static List<Candle> M5Rise(decimal finalMove) =>
        Make(Enumerable.Range(0, 60).Select(i => 1.0000m + 0.0005m * i).Append(1.0000m + 0.0005m * 59 + finalMove));

    [Fact]
    public void Uptrend_PlusRsiLeavingOversold_GivesBuy()
    {
        var signal = SignalEngine.Evaluate(M5Fall(0.004m), Trend(1.0m, 0.0002m, 250));
        Assert.NotNull(signal);
        Assert.Equal(Side.Buy, signal!.Side);
        Assert.True(signal.Atr > 0);
    }

    [Fact]
    public void Downtrend_PlusRsiLeavingOverbought_GivesSell()
    {
        var signal = SignalEngine.Evaluate(M5Rise(-0.004m), Trend(1.2m, -0.0002m, 250));
        Assert.NotNull(signal);
        Assert.Equal(Side.Sell, signal!.Side);
    }

    [Fact]
    public void CounterTrendSetup_IsIgnored()
    {
        // Oversold bounce, but the 15m trend is down.
        Assert.Null(SignalEngine.Evaluate(M5Fall(0.004m), Trend(1.2m, -0.0002m, 250)));
    }

    [Fact]
    public void CounterTrendSetup_ExplainsWhyThereIsNoTrade()
    {
        var evaluation = SignalEngine.EvaluateWithReason(M5Fall(0.004m), Trend(1.2m, -0.0002m, 250));

        Assert.Null(evaluation.Signal);
        Assert.Contains("15-minute trend is down", evaluation.Explanation);
        Assert.Contains("RSI", evaluation.Explanation);
    }

    [Fact]
    public void NoTrigger_NoSignal()
    {
        // Still falling (RSI stays below 30, never crosses back up).
        Assert.Null(SignalEngine.Evaluate(M5Fall(-0.0005m), Trend(1.0m, 0.0002m, 250)));
    }

    [Fact]
    public void InsufficientHistory_NoSignal()
    {
        Assert.Null(SignalEngine.Evaluate(M5Fall(0.004m), Trend(1.0m, 0.0002m, 150)));
    }

    [Fact]
    public void InsufficientHistory_ExplainsWhatIsMissing()
    {
        var evaluation = SignalEngine.EvaluateWithReason(M5Fall(0.004m), Trend(1.0m, 0.0002m, 150));

        Assert.Null(evaluation.Signal);
        Assert.Contains("15-minute history", evaluation.Explanation);
        Assert.Contains("200 candles", evaluation.Explanation);
    }
}

public class NewsWindowTests
{
    private static readonly DateTime Release = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static NewsEvent Ev(string ccy, string impact) => new(Release, ccy, "CPI", impact);

    [Theory]
    [InlineData(-31, false)]
    [InlineData(-30, true)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void ThirtyMinuteWindow_BothSides(int minutesFromRelease, bool blocked)
    {
        var now = Release.AddMinutes(minutesFromRelease);
        Assert.Equal(blocked, NewsWindow.FindBlockingEvent(new[] { Ev("USD", "High") }, "EUR_USD", now) is not null);
    }

    [Fact]
    public void OnlyHighImpact_AndOnlyRelevantCurrencies_Block()
    {
        Assert.Null(NewsWindow.FindBlockingEvent(new[] { Ev("USD", "Medium") }, "EUR_USD", Release));
        Assert.Null(NewsWindow.FindBlockingEvent(new[] { Ev("JPY", "High") }, "EUR_USD", Release));
        Assert.NotNull(NewsWindow.FindBlockingEvent(new[] { Ev("EUR", "High") }, "EUR_USD", Release));
        Assert.NotNull(NewsWindow.FindBlockingEvent(new[] { Ev("GBP", "high") }, "GBP_USD", Release));
    }

    [Fact]
    public void Spread_IsMeasuredInPips()
    {
        var q = new Quote("EUR_USD", 1.08500m, 1.08504m, DateTime.UtcNow, true);
        Assert.Equal(0.4m, GatekeeperAgent.SpreadPips(q, "EUR_USD"));
    }
}
