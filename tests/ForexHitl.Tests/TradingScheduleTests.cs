using ForexHitl;

namespace ForexHitl.Tests;

public class TradingScheduleTests
{
    [Theory]
    [InlineData("2026-10-18T21:59:59Z", false)] // Monday 08:59:59 AEDT
    [InlineData("2026-10-18T22:00:00Z", true)]  // Monday 09:00 AEDT
    [InlineData("2026-10-19T05:59:59Z", true)]
    [InlineData("2026-10-19T06:00:00Z", false)] // 17:00 is excluded
    [InlineData("2026-10-23T23:00:00Z", false)] // Saturday
    [InlineData("2026-10-24T23:00:00Z", false)] // Sunday
    [InlineData("2026-07-12T23:00:00Z", true)]  // Monday 09:00 AEST
    [InlineData("2026-07-13T07:00:00Z", false)] // Monday 17:00 AEST
    public void MelbourneHours_HandleBoundariesWeekendsAndBothOffsets(string utc, bool expected) =>
        Assert.Equal(expected, new TradingSchedule().IsOpen(DateTimeOffset.Parse(utc).UtcDateTime));

    [Fact]
    public void CustomHoursAndDays_AreRespected()
    {
        var schedule = new TradingSchedule("UTC", "10:30", "12:45", "Sunday");
        Assert.True(schedule.IsOpen(new DateTime(2026, 10, 18, 11, 0, 0, DateTimeKind.Utc)));
        Assert.False(schedule.IsOpen(new DateTime(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("17:00", "09:00", "Monday")]
    [InlineData("09:00", "09:00", "Monday")]
    [InlineData("bad", "17:00", "Monday")]
    [InlineData("09:00", "17:00", "")]
    [InlineData("09:00", "17:00", "1")]
    [InlineData("09:00", "17:00", "Mondy")]
    public void BadConfiguration_StopsStartup(string start, string end, string days) =>
        Assert.Throws<InvalidOperationException>(() => new TradingSchedule("UTC", start, end, days));

    [Fact]
    public async Task ClosedHours_BlockProposalsAndShowPausedStatus()
    {
        var h = new Harness();
        h.Clock.SetUtc(DateTimeOffset.Parse("2026-10-19T06:00:00Z"));
        await h.Propose();
        var status = await h.Agent.RefreshGuardAsync(CancellationToken.None);
        Assert.Empty(h.Store.Snapshot());
        Assert.False(status.CanTrade);
        Assert.Contains("Outside trading hours", h.Agent.LastStatus!.Reason);
    }

    [Fact]
    public async Task ProposalCreatedBeforeClose_CannotBeApprovedAtClose()
    {
        var h = new Harness();
        h.Clock.SetUtc(DateTimeOffset.Parse("2026-10-19T05:59:00Z"));
        var proposal = await h.ProposeOne();
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var result = await h.Decide("approve", proposal.Id);
        Assert.False(result.Executed);
        Assert.Contains("Outside trading hours", result.Message);
        Assert.Empty(h.Broker.Orders);
    }

    [Fact]
    public async Task ChecksHoursAgainAfterSlowNetworkWork()
    {
        var store = new MemoryStateStore();
        var h = new Harness(state: store);
        h.Clock.SetUtc(DateTimeOffset.Parse("2026-10-19T05:59:00Z"));
        var proposal = await h.ProposeOne();
        store.OnSave = () => h.Clock.Advance(TimeSpan.FromMinutes(1));
        var result = await h.Decide("approve", proposal.Id);
        Assert.False(result.Executed);
        Assert.Empty(h.Broker.Orders);
    }
}
