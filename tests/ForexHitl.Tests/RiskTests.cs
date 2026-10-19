using ForexHitl;

namespace ForexHitl.Tests;

public class RiskManagerTests
{
    [Fact]
    public void TenPipStop_OnTenThousand_IsHalfALot()
    {
        Assert.Equal(0.50m, RiskManager.CalculateLotSize(10_000m, 10m));
        Assert.Equal(50m, RiskManager.AllowedDollarRisk(10_000m));
    }

    [Fact]
    public void LargeBalance_IsCappedAtFiftyAccountCurrencyUnits()
    {
        Assert.Equal(50m, RiskManager.AllowedDollarRisk(100_000m));
        var units = RiskManager.CalculateUnits("EUR_USD", 100_000m, 9m, 1.085m);
        Assert.True(RiskManager.RiskUsd("EUR_USD", units, 9m) <= 50m);
        Assert.True(RiskManager.RiskUsd("EUR_USD", units, 9m) > 49m);
    }

    [Fact]
    public void ConfiguredRiskCap_IsAppliedToSizing()
    {
        Assert.Equal(25m, RiskManager.AllowedDollarRisk(100_000m, 25m));
        var units = RiskManager.CalculateUnits("EUR_USD", 100_000m, 9m, 1.085m, 1m, 25m);
        Assert.True(RiskManager.RiskUsd("EUR_USD", units, 9m) <= 25m);
    }

    [Fact]
    public void LotSize_RoundsDown_SoRiskNeverExceedsHalfPercent()
    {
        // 3 pip stop: exact 1.6667 lots. Math.Round would give 1.67 ($50.10 risk); we must give 1.66.
        Assert.Equal(1.66m, RiskManager.CalculateLotSize(10_000m, 3m));
    }

    [Fact]
    public void Units_NeverRiskMoreThanFiftyDollars_AcrossStopRange()
    {
        for (var pips = 8m; pips <= 40m; pips += 0.5m)
        {
            var units = RiskManager.CalculateUnits("EUR_USD", 10_000m, pips, 1.0850m);
            Assert.True(units > 0);
            Assert.True(RiskManager.RiskUsd("EUR_USD", units, pips) <= 50m, $"risk exceeded at {pips} pips");
        }
    }

    [Fact]
    public void Units_AreCappedByLeverage()
    {
        // Risk alone would allow 500,000 units on a 1 pip stop; 10x leverage caps it.
        var units = RiskManager.CalculateUnits("EUR_USD", 10_000m, 1m, 1.10m);
        Assert.Equal(90_909, units);
    }

    [Fact]
    public void ZeroStop_Throws()
    {
        Assert.Throws<ArgumentException>(() => RiskManager.CalculateLotSize(10_000m, 0m));
        Assert.Throws<ArgumentException>(() => RiskManager.CalculateUnits("EUR_USD", 10_000m, 0m, 1.1m));
    }

    [Fact]
    public void ProposalBuilder_GivesExactlyOneToTwo_AndRespectsRisk()
    {
        var quote = new Quote("EUR_USD", 1.08500m, 1.08504m, DateTime.UtcNow, true);
        var signal = new Signal(Side.Buy, 0.00060m, "test");

        var p = ProposalBuilder.Build("EUR_USD", signal, quote, 10_000m, DateTime.UtcNow)!;

        Assert.Equal(quote.Ask, p.EntryPrice);
        Assert.Equal(9m, p.StopLossPips);                              // 1.5 x ATR = 9 pips
        Assert.Equal(2m, (p.TakeProfit - p.EntryPrice) / (p.EntryPrice - p.StopLoss));
        Assert.True(p.RiskUsd <= 50m);
    }

    [Fact]
    public void ProposalBuilder_Sell_UsesBidAndInvertsLevels()
    {
        var quote = new Quote("GBP_USD", 1.30000m, 1.30004m, DateTime.UtcNow, true);
        var p = ProposalBuilder.Build("GBP_USD", new Signal(Side.Sell, 0.00100m, "t"), quote, 10_000m, DateTime.UtcNow)!;

        Assert.Equal(quote.Bid, p.EntryPrice);
        Assert.True(p.StopLoss > p.EntryPrice);
        Assert.True(p.TakeProfit < p.EntryPrice);
    }

    [Fact]
    public void Reanchor_PreservesDistancesAndRisk()
    {
        var quote = new Quote("EUR_USD", 1.08500m, 1.08504m, DateTime.UtcNow, true);
        var p = ProposalBuilder.Build("EUR_USD", new Signal(Side.Buy, 0.00060m, "t"), quote, 10_000m, DateTime.UtcNow)!;

        var moved = ProposalBuilder.Reanchor(p, 1.08520m);

        Assert.Equal(p.EntryPrice - p.StopLoss, moved.EntryPrice - moved.StopLoss);
        Assert.Equal(p.TakeProfit - p.EntryPrice, moved.TakeProfit - moved.EntryPrice);
        Assert.Equal(p.Units, moved.Units);
    }
}

public class DailyRiskGuardTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DrawdownCap_LocksFor24Hours()
    {
        var g = new DailyRiskGuard();
        Assert.True(g.Evaluate(10_000m, T0).CanTrade);
        Assert.True(g.Evaluate(9_851m, T0.AddHours(1)).CanTrade);        // -$149: still allowed

        var hit = g.Evaluate(9_850m, T0.AddHours(2));                    // -$150: locked
        Assert.False(hit.CanTrade);
        Assert.True(hit.NewlyLocked);

        var later = g.Evaluate(9_850m, T0.AddHours(3));
        Assert.False(later.CanTrade);
        Assert.False(later.NewlyLocked);

        Assert.False(g.Evaluate(10_000m, T0.AddHours(25)).CanTrade);     // still inside the 24h lockout
        Assert.True(g.Evaluate(10_000m, T0.AddHours(27)).CanTrade);      // lockout over
    }

    [Fact]
    public void ProfitCap_StopsTradingUntilNextSession()
    {
        var g = new DailyRiskGuard();
        g.Evaluate(10_000m, T0);

        var hit = g.Evaluate(10_100m, T0.AddHours(1));
        Assert.False(hit.CanTrade);
        Assert.True(hit.NewlyLocked);

        Assert.False(g.Evaluate(10_100m, T0.AddHours(5)).CanTrade);
        Assert.True(g.Evaluate(10_100m, new DateTime(2026, 10, 14, 22, 1, 0, DateTimeKind.Utc)).CanTrade);
    }

    [Fact]
    public void RestoredState_ContinuesWhereTheLastRunStopped()
    {
        var first = new DailyRiskGuard();
        first.Evaluate(10_000m, T0);
        Assert.False(first.Evaluate(9_800m, T0.AddHours(1)).CanTrade);
        var saved = first.TakeDirtySnapshot()!;

        var second = new DailyRiskGuard(awaitingRestore: true);
        second.Restore(saved);
        Assert.False(second.Evaluate(9_800m, T0.AddHours(2)).CanTrade);   // still locked after the "restart"
    }

    [Fact]
    public void GuardAwaitingRestore_RefusesToTradeUntilRestored()
    {
        var g = new DailyRiskGuard(awaitingRestore: true);
        Assert.False(g.Ready);

        var before = g.Evaluate(10_000m, T0);
        Assert.False(before.CanTrade);
        Assert.Equal(DailyRiskGuard.AwaitingRestoreReason, before.Reason);
        Assert.Null(g.TakeDirtySnapshot());          // nothing may be saved from a guard that never loaded

        g.Restore(null);
        Assert.True(g.Ready);
        Assert.True(g.Evaluate(10_000m, T0).CanTrade);
    }

    [Fact]
    public void OnlyChangedStateIsMarkedForSaving()
    {
        var g = new DailyRiskGuard();
        g.Evaluate(10_000m, T0);                     // sets the day's baseline: a change
        Assert.NotNull(g.TakeDirtySnapshot());

        g.Evaluate(10_010m, T0.AddMinutes(5));       // nothing changed
        Assert.Null(g.TakeDirtySnapshot());

        g.MarkDirty();                               // e.g. a failed save
        Assert.NotNull(g.TakeDirtySnapshot());
    }

    [Fact]
    public void SessionStart_RollsAtTwentyTwoUtc()
    {
        Assert.Equal(new DateTime(2026, 10, 13, 22, 0, 0, DateTimeKind.Utc), DailyRiskGuard.SessionStart(T0));
        Assert.Equal(new DateTime(2026, 10, 14, 22, 0, 0, DateTimeKind.Utc),
            DailyRiskGuard.SessionStart(new DateTime(2026, 10, 14, 22, 0, 0, DateTimeKind.Utc)));
    }
}
