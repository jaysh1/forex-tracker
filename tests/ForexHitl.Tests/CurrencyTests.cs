using ForexHitl;

namespace ForexHitl.Tests;

public class AccountCurrencyTests
{
    private static Quote Q(string pair, decimal bid, decimal ask) => new(pair, bid, ask, DateTime.UtcNow, true);

    [Fact]
    public void PairNames_FollowOandaConventions()
    {
        Assert.Equal("AUD_USD", AccountCurrency.PairFor("AUD"));
        Assert.Equal("EUR_USD", AccountCurrency.PairFor("eur"));
        Assert.Equal("USD_CAD", AccountCurrency.PairFor("CAD"));
        Assert.Equal("USD_JPY", AccountCurrency.PairFor("JPY"));
    }

    [Fact]
    public void Aud_UsesInverseOfTheLowerPrice_SoRiskErrsLarge()
    {
        var fx = AccountCurrency.UsdToAccount("AUD", Q("AUD_USD", 0.65000m, 0.65004m));
        Assert.Equal(1m / 0.65000m, fx);
        Assert.True(fx > 1m / 0.65004m);
    }

    [Fact]
    public void UsdQuotedHomeCurrency_UsesTheHigherPrice()
    {
        Assert.Equal(1.40004m, AccountCurrency.UsdToAccount("CAD", Q("USD_CAD", 1.40000m, 1.40004m)));
    }

    [Fact]
    public void UnusablePrice_Throws()
    {
        Assert.Throws<BrokerException>(() => AccountCurrency.UsdToAccount("AUD", Q("AUD_USD", 0m, 0m)));
    }
}

public class AudAccountSizingTests
{
    private static readonly decimal AudFx = 1m / 0.65000m;

    [Fact]
    public void UnitsAreSmallerOnAnAudAccount_AndRiskIsMeasuredInAud()
    {
        var usd = RiskManager.CalculateUnits("EUR_USD", 100_000m, 9m, 1.085m);
        var aud = RiskManager.CalculateUnits("EUR_USD", 100_000m, 9m, 1.085m, AudFx);

        Assert.True(aud < usd);
        var riskAud = RiskManager.RiskUsd("EUR_USD", aud, 9m, AudFx);
        Assert.True(riskAud <= 50m);
        Assert.True(riskAud > 49m);

        // A USD-sized position converts to more than the same 50-unit account-currency cap in AUD.
        Assert.True(RiskManager.RiskUsd("EUR_USD", usd, 9m, AudFx) > 75m);
    }

    [Fact]
    public void ConversionRateMustBePositive()
    {
        Assert.Throws<ArgumentException>(() => RiskManager.CalculateUnits("EUR_USD", 100_000m, 9m, 1.085m, 0m));
    }

    [Fact]
    public async Task Proposal_OnAnAudAccount_RisksNoMoreThanHalfAPercentOfAudEquity()
    {
        var h = new Harness();
        h.Broker.Currency = "AUD";
        h.Broker.Nav = 100_000m;

        var p = await h.ProposeOne();

        Assert.True(p.RiskUsd <= 50m);
        Assert.True(p.RiskUsd > 49m);
        Assert.True(p.Units < 400_000);     // naive USD sizing would give ~555,000
    }

    [Fact]
    public async Task Approve_OnAnAudAccount_PlacesAnOrderWithinBudget()
    {
        var h = new Harness();
        h.Broker.Currency = "AUD";
        h.Broker.Nav = 100_000m;
        var p = await h.ProposeOne();

        var result = await h.Decide("approve", p.Id);

        Assert.True(result.Executed);
        var o = h.Broker.Orders.Single();
        Assert.True(RiskManager.RiskUsd("EUR_USD", o.Units, o.StopLossPips, AudFx) <= 50m);
    }

    [Fact]
    public async Task Approve_AfterBigExchangeRateMove_IsRefused()
    {
        var h = new Harness();
        h.Broker.Currency = "AUD";
        h.Broker.Nav = 100_000m;
        var p = await h.ProposeOne();

        h.Broker.AudUsdBid = 0.58500m;      // AUD down 10%: the same units would now risk ~11% more
        h.Broker.AudUsdAsk = 0.58504m;

        var result = await h.Decide("approve", p.Id);

        Assert.False(result.Executed);
        Assert.Contains("0.5% risk", result.Message);
        Assert.Equal(0, h.Broker.Orders.Count);
    }

    [Fact]
    public async Task Approve_AfterSmallExchangeRateMove_IsDownsizedWithinBudget()
    {
        var h = new Harness();
        h.Broker.Currency = "AUD";
        h.Broker.Nav = 100_000m;
        var p = await h.ProposeOne();

        h.Broker.AudUsdBid = 0.64500m;      // AUD down ~0.8%
        h.Broker.AudUsdAsk = 0.64504m;

        var result = await h.Decide("approve", p.Id);

        Assert.True(result.Executed);
        var o = h.Broker.Orders.Single();
        Assert.True(o.Units < p.Units);
        Assert.True(RiskManager.RiskUsd("EUR_USD", o.Units, o.StopLossPips, 1m / 0.64500m) <= 50m);
    }
}
