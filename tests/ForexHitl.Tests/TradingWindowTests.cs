using ForexHitl;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForexHitl.Tests;

public class TradingWindowTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static TradingWindow Default => TradingWindow.Parse(null, null, null);

    private static void Rejects(string? hours, string? days, string? zone)
    {
        var threw = false;
        try { TradingWindow.Parse(hours, days, zone); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw, $"expected rejection of {hours} / {days} / {zone}");
    }

    [Fact]
    public void Defaults_AreMondayToFriday_NineToFive_Melbourne()
    {
        Assert.Equal("Mon-Fri 09:00-17:00 Australia/Melbourne", Default.Describe());
        Assert.False(Default.Unrestricted);
    }

    [Fact]
    public void Summer_StartIsInclusive_EndIsExclusive()
    {
        // Wednesday 21 Oct 2026 is AEDT (UTC+11).
        Assert.False(Default.IsOpen(Utc(2026, 10, 20, 21, 59)));   // 08:59 local
        Assert.True(Default.IsOpen(Utc(2026, 10, 20, 22, 0)));     // 09:00 local
        Assert.True(Default.IsOpen(Utc(2026, 10, 21, 5, 59)));     // 16:59 local
        Assert.False(Default.IsOpen(Utc(2026, 10, 21, 6, 0)));     // 17:00 local
    }

    [Fact]
    public void Winter_FollowsDaylightSaving_NotAFixedOffset()
    {
        // Wednesday 15 Jul 2026 is AEST (UTC+10).
        Assert.False(Default.IsOpen(Utc(2026, 7, 14, 22, 0)));     // 08:00 local (would be 09:00 under +11)
        Assert.True(Default.IsOpen(Utc(2026, 7, 14, 23, 0)));      // 09:00 local
        Assert.True(Default.IsOpen(Utc(2026, 7, 15, 6, 59)));      // 16:59 local
        Assert.False(Default.IsOpen(Utc(2026, 7, 15, 7, 0)));      // 17:00 local
    }

    [Fact]
    public void Weekends_AreClosed_AndTheDayIsTakenFromLocalTime()
    {
        Assert.False(Default.IsOpen(Utc(2026, 10, 17, 1, 0)));     // Sat 12:00 local
        Assert.False(Default.IsOpen(Utc(2026, 10, 18, 1, 0)));     // Sun 12:00 local

        Assert.True(Default.IsOpen(Utc(2026, 10, 18, 22, 0)));     // Mon 09:00 local is still Sunday in UTC
        Assert.True(Default.IsOpen(Utc(2026, 10, 23, 5, 59)));     // Fri 16:59 local
        Assert.False(Default.IsOpen(Utc(2026, 10, 23, 6, 0)));     // Fri 17:00 local
        Assert.False(Default.IsOpen(Utc(2026, 10, 16, 22, 0)));    // Sat 09:00 local is still Friday in UTC
    }

    [Fact]
    public void CustomDays_AreHonoured_AndDescribedCompactly()
    {
        var w = TradingWindow.Parse("09:00-17:00", "Mon,Wed,Fri", "Australia/Melbourne");
        Assert.Equal("Mon,Wed,Fri 09:00-17:00 Australia/Melbourne", w.Describe());
        Assert.True(w.IsOpen(Utc(2026, 10, 21, 2, 0)));            // Wed 13:00 local
        Assert.False(w.IsOpen(Utc(2026, 10, 20, 2, 0)));           // Tue 13:00 local

        Assert.Equal("Mon-Wed,Fri 09:00-17:00 Australia/Melbourne",
            TradingWindow.Parse("09:00-17:00", "Mon-Wed,Fri", null).Describe());
        Assert.Equal("Sat,Sun 09:00-17:00 Australia/Melbourne",
            TradingWindow.Parse("09:00-17:00", "sat,sun", null).Describe());
    }

    [Fact]
    public void DayRange_CanWrapPastSunday()
    {
        var w = TradingWindow.Parse("09:00-17:00", "Fri-Mon", null);
        Assert.Equal("Mon,Fri-Sun 09:00-17:00 Australia/Melbourne", w.Describe());
        Assert.True(w.IsOpen(Utc(2026, 10, 17, 2, 0)));            // Sat 13:00 local
        Assert.False(w.IsOpen(Utc(2026, 10, 21, 2, 0)));           // Wed 13:00 local
    }

    [Fact]
    public void OtherHoursAndZones_Work()
    {
        var w = TradingWindow.Parse("8:30-12:00", "Mon-Fri", "UTC");
        Assert.Equal("Mon-Fri 08:30-12:00 UTC", w.Describe());
        Assert.False(w.IsOpen(Utc(2026, 10, 21, 8, 29)));
        Assert.True(w.IsOpen(Utc(2026, 10, 21, 8, 30)));
        Assert.False(w.IsOpen(Utc(2026, 10, 21, 12, 0)));
    }

    [Fact]
    public void Always_NeverRestricts()
    {
        var w = TradingWindow.Parse("always", null, null);
        Assert.True(w.Unrestricted);
        Assert.True(w.IsOpen(Utc(2026, 10, 17, 1, 0)));            // a Saturday
        Assert.Equal("any time", w.Describe());
        Assert.True(TradingWindow.Always.IsOpen(Utc(2026, 10, 17, 1, 0)));
    }

    [Fact]
    public void UnspecifiedKind_IsTreatedAsUtc()
    {
        Assert.True(Default.IsOpen(new DateTime(2026, 10, 20, 22, 0, 0)));
    }

    [Fact]
    public void BadSettings_AreRejectedWithAnError()
    {
        Rejects("9-5", null, null);
        Rejects("17:00-09:00", null, null);       // would cross midnight
        Rejects("09:00-09:00", null, null);
        Rejects("25:00-26:00", null, null);
        Rejects("09:00-17:00", "Funday", null);
        Rejects("09:00-17:00", ",", null);
        Rejects("09:00-17:00", null, "Mars/Olympus");
    }
}

public class TradingHoursEnforcementTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 21, 2, 0, 0, TimeSpan.Zero);      // Wed 13:00 Melbourne
    private static readonly DateTimeOffset Closed = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);   // Wed 21:00 Melbourne
    private static readonly DateTimeOffset LastMinutes = new(2026, 10, 21, 5, 58, 0, TimeSpan.Zero); // Wed 16:58 Melbourne

    private static Harness MakeHarness(DateTimeOffset now)
    {
        var h = new Harness(TradingWindow.Parse(null, null, null));
        h.Clock.Set(now);
        return h;
    }

    [Fact]
    public async Task Signal_InsideHours_IsProposed()
    {
        var h = MakeHarness(Open);
        await h.Propose();
        Assert.Equal(1, h.Store.Snapshot().Count);
    }

    [Fact]
    public async Task Signal_OutsideHours_QueuesNothing()
    {
        var h = MakeHarness(Closed);
        await h.Propose();

        Assert.Equal(0, h.Store.Snapshot().Count);
        Assert.Equal(0, h.Notifier.Titles.Count);
    }

    [Fact]
    public async Task ApproveAfterTheWindowCloses_IsRefused_AndPlacesNothing()
    {
        var h = MakeHarness(LastMinutes);
        var p = await h.ProposeOne();
        h.Clock.Advance(TimeSpan.FromMinutes(3));                    // now 17:01 Melbourne, inside the 5 min expiry

        var result = await h.Decide("approve", p.Id);

        Assert.False(result.Executed);
        Assert.Contains("Outside trading hours", result.Message);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task WindowClosesWhileTheApprovalChecksRun_PlacesNothing()
    {
        var h = MakeHarness(LastMinutes);
        var p = await h.ProposeOne();
        // Inside hours when approval starts; the clock passes 17:00 during the position check just before the order.
        h.Broker.OnHasOpenPosition = () => h.Clock.Advance(TimeSpan.FromMinutes(3));

        var result = await h.Decide("approve", p.Id);

        Assert.False(result.Executed);
        Assert.Contains("Outside trading hours", result.Message);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task RejectStillWorks_AfterTheWindowCloses()
    {
        var h = MakeHarness(LastMinutes);
        var p = await h.ProposeOne();
        h.Clock.Advance(TimeSpan.FromMinutes(3));

        var result = await h.Decide("reject", p.Id);

        Assert.Equal("Rejected.", result.Message);
        Assert.Equal(0, h.Store.Snapshot().Count);
    }

    [Fact]
    public async Task ApproveInsideHours_StillWorks()
    {
        var h = MakeHarness(Open);
        var p = await h.ProposeOne();
        var result = await h.Decide("approve", p.Id);

        Assert.True(result.Executed);
        Assert.Equal(1, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task Scanner_OutsideHours_SkipsCandles_ButKeepsTheStatusFresh_AndLogsTransitions()
    {
        var h = MakeHarness(Closed);
        var env = new Dictionary<string, string>
        {
            ["OANDA_API_TOKEN"] = "t", ["OANDA_ACCOUNT_ID"] = "101-001-1-001", ["APPROVER_PASSWORD"] = "a-long-enough-password"
        };
        var config = AppConfig.FromEnvironment(k => env.GetValueOrDefault(k));
        var scanner = new TechnicalAnalyticsAgent(
            h.Broker, h.Agent, config, h.Activity, config.TradingHours, h.Clock,
            NullLogger<TechnicalAnalyticsAgent>.Instance);

        await scanner.TickAsync(CancellationToken.None);
        Assert.Equal(0, h.Broker.CandleCalls);
        Assert.NotNull(h.Agent.LastStatus);                          // the page still gets account figures
        Assert.True(h.ActivityMentions("Outside trading hours"));

        await scanner.TickAsync(CancellationToken.None);             // no change: no repeated message
        Assert.Equal(1, h.Activity.Snapshot().Count(a => a.Kind == "hours"));

        h.Clock.Set(Open);
        await scanner.TickAsync(CancellationToken.None);
        Assert.Equal(2, h.Broker.CandleCalls);                       // one M5 request per instrument
        Assert.True(h.ActivityMentions("Trading hours started"));
        Assert.Equal(2, h.Activity.Snapshot().Count(a => a.Kind == "hours"));
    }
}
