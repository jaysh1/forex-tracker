namespace ForexHitl;

/// <summary>Hardcoded, non-negotiable risk parameters. Change only by editing code and redeploying.</summary>
public static class RiskLimits
{
    /// <summary>Paper-account starting balance. Live sizing always uses the broker's current NAV.</summary>
    public const decimal ReferenceBalance = 10_000m;

    public const decimal RiskPerTradePct = 0.5m;        // $50 on $10k
    public const decimal DailyDrawdownPct = 1.5m;       // $150 on $10k
    public const decimal DailyProfitTargetPct = 1.0m;   // $100 on $10k
    public const decimal MinRewardToRisk = 2.0m;        // 1:2

    public const decimal MaxSpreadPips = 0.5m;

    /// <summary>Extra safety rail (not in the original spec): cap on notional / equity.</summary>
    public const decimal MaxLeverage = 10m;

    public static readonly TimeSpan NewsBlackout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DrawdownLockout = TimeSpan.FromHours(24);
}
