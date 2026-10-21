using CFDEngine;
using ForexHitl;

namespace ForexHitl.Tests;

public class DashboardPresentationTests
{
    [Fact]
    public void ForexPresenter_ProvidesFormattedValuesAndServerOwnedConfirmationState()
    {
        var now = new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);
        var presenter = new DashboardPresenter();
        var hours = TradingWindow.Parse("09:00-17:00", "Mon-Fri", "Australia/Melbourne");
        var confirmations = new DecisionConfirmationStore();
        Assert.False(confirmations.TryConfirm("forex", "p1", "request-1", now));

        var model = presenter.Forex(false,
            new StatusSnapshot(now, 100_000m, "AUD", 25.5m, true, null), hours, now,
            new[] { new InstrumentScanDto("EUR_USD", 1.085m, 1.08504m, true, now, now, "RSI has not crossed its trigger.") },
            new[] { new TradeProposal("p1", "EUR_USD", Side.Buy, 1.08504m, 1.08414m,
                1.08684m, 9m, 100_000, 50m, now, "test rationale") },
            new[] { new ActivityEntry(now, "proposal", "Proposal created.") }, confirmations);

        Assert.Equal("PRACTICE", model.ModeLabel);
        Assert.Equal("AUD 100,000.00", model.EquityText);
        Assert.Equal("+AUD 25.50", model.TodayText);
        Assert.Equal("EUR/USD", Assert.Single(model.Instruments).Instrument);
        Assert.Equal("1.08500", model.Instruments[0].BidText);
        Assert.Contains("1.00 lot", Assert.Single(model.Pending).SizeText);
        Assert.True(model.Pending[0].ConfirmationArmed);
        Assert.Equal("AUD 50.00", model.Pending[0].RiskText);
        Assert.True(model.HasActivity);
    }

    [Fact]
    public void CfdPresenter_FormatsProposalFieldsOnServer()
    {
        var presenter = new DashboardPresenter();
        var state = new CfdStatusDto(true, "practice", new[] { "AU200_AUD" }, DateTime.UtcNow,
            100_000m, "AUD", -10m, true, null, "any time",
            new[] { new CfdPendingDto("c1", "AU200_AUD", "Buy", 2, 7900.5m, 7888.5m,
                7924.5m, 12m, 48m, "AUD", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5),
                "test CFD rationale", 1) });

        var model = presenter.Cfd(state, new DecisionConfirmationStore(), DateTime.UtcNow);

        Assert.Contains("AU200_AUD", model.EnabledText);
        Assert.Equal("AUD 100,000.00", model.EquityText);
        Assert.Equal("2", Assert.Single(model.Pending).UnitsText);
        Assert.Equal("7900.5 AUD", model.Pending[0].EntryText);
        Assert.Equal("AUD 48.00", model.Pending[0].MaxLossText);
    }
}

public class DecisionConfirmationStoreTests
{
    [Fact]
    public void ApprovalRequiresSecondServerConfirmationWithinTwentySeconds()
    {
        var store = new DecisionConfirmationStore();
        var now = DateTime.UtcNow;

        Assert.False(store.TryConfirm("forex", "p1", "request-1", now));
        Assert.True(store.IsArmed("forex", "p1", now.AddSeconds(1)));
        Assert.False(store.TryConfirm("forex", "p1", "request-1", now.AddSeconds(1)));
        Assert.False(store.TryConfirm("forex", "p1", "request-2", now.AddMilliseconds(500)));
        Assert.False(store.TryConfirm("forex", "p1", "request-3", now.AddSeconds(20)));
        Assert.True(store.TryConfirm("forex", "p1", "request-4", now.AddSeconds(21)));
    }
}
