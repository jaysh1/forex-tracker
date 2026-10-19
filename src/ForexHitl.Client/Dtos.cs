namespace ForexHitl.Client;

// Mirror of src/ForexHitl/Approval/ApiModels.cs - keep the two in sync.

public sealed record LoginResponse(string Token);

public sealed record DecisionResult(bool Executed, string Message);

public sealed record PendingDto(
    string Id, string Instrument, string Side, long Units,
    decimal Entry, decimal StopLoss, decimal TakeProfit, decimal StopLossPips, decimal RiskUsd,
    DateTime CreatedUtc, DateTime ExpiresUtc, string Rationale);

public sealed record StatusDto(
    string Mode, bool Ready, DateTime? UpdatedUtc, decimal? Equity, decimal? DayPnl,
    bool CanTrade, string? Reason, DateTime ServerTimeUtc, string TradingHours);

public sealed record ActivityDto(DateTime TimeUtc, string Kind, string Text);

public sealed record StateDto(StatusDto Status, List<PendingDto> Pending, List<ActivityDto> Activity);
