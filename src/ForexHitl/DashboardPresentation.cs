using System.Globalization;
using CFDEngine;

namespace ForexHitl;

/// <summary>Display-ready API contracts: the Vue client renders these values without domain calculations.</summary>
public sealed record ForexDashboardDto(
    string ModeLabel, string ModeClass, bool Ready, string NotReadyText,
    string EquityText, string TodayText, string TodayClass,
    string TradingText, string HoursText, string? TradingWarning, string? ScannerWarning,
    bool HasInstruments, IReadOnlyList<MarketCheckView> Instruments,
    bool HasPending, IReadOnlyList<ForexProposalView> Pending,
    bool HasActivity, IReadOnlyList<ActivityView> Activity,
    string NoMarketChecksText, string NoPendingText, string NoActivityText);

public sealed record MarketCheckView(
    string Instrument, string BidText, string AskText, string Explanation,
    string PriceTimeText, string ReasonUpdatedText);

public sealed record ForexProposalView(
    string Id, string Instrument, string SideLabel, string SideClass, bool ConfirmationArmed,
    string SizeText, string EntryText, string StopText, string TakeProfitText,
    string RiskText, string ExpiresText, string Rationale);

public sealed record ActivityView(string TimeText, string KindClass, string Text);

public sealed record CfdDashboardDto(
    bool Enabled, string EnabledText, string EquityText, string DayPnlText, string? Reason,
    bool HasPending, IReadOnlyList<CfdProposalView> Pending, string NoPendingText);

public sealed record CfdProposalView(
    string Id, string Instrument, string SideLabel, string SideClass, bool ConfirmationArmed,
    string UnitsText, string EntryText, string StopText, string TakeProfitText,
    string MaxLossText, string ExpiresText, string Rationale);

public sealed record DashboardActionResult(bool Succeeded, bool Executed, bool NeedsConfirmation, string Message);

/// <summary>Server-side formatting and presentation decisions for the thin dashboard client.</summary>
public sealed class DashboardPresenter
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.InvariantCulture;
    private readonly TimeZoneInfo _displayZone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Melbourne");

    public ForexDashboardDto Forex(
        bool live, StatusSnapshot? status, TradingWindow hours, DateTime nowUtc,
        IReadOnlyList<InstrumentScanDto> instruments, IReadOnlyList<TradeProposal> pending,
        IReadOnlyList<ActivityEntry> activity, DecisionConfirmationStore confirmations)
    {
        var dayPnl = status?.DayPnl;
        var scannerWarning = status?.UpdatedUtc is { } updated && nowUtc - updated > TimeSpan.FromSeconds(90)
            ? $"Scanner has not reported for {Math.Max(1, (int)(nowUtc - updated).TotalMinutes)} min. It may be down or unable to reach OANDA; the figures above could be out of date. Check the server logs."
            : null;

        return new ForexDashboardDto(
            live ? "LIVE" : "PRACTICE", live ? "live" : "practice", status is not null,
            "Waiting for the first update from OANDA. If this lasts more than a minute, check the scanner activity and OANDA connection.",
            Money(status?.Equity, status?.Currency), SignedMoney(dayPnl, status?.Currency),
            dayPnl switch { > 0 => "pos", < 0 => "neg", _ => "" },
            status?.CanTrade == true ? "Active" : "Paused",
            $"{(hours.IsOpen(nowUtc) ? "Open now" : "Closed now")} ({hours.Describe()})",
            status is { CanTrade: false } ? status.Reason : null, scannerWarning,
            instruments.Count > 0, instruments.Select(Instrument).ToArray(),
            pending.Count > 0, pending.Select(p => Proposal(p, status?.Currency,
                confirmations.IsArmed("forex", p.Id, nowUtc))).ToArray(),
            activity.Count > 0, activity.Select(a => new ActivityView(LocalTime(a.TimeUtc), a.Kind, a.Text)).ToArray(),
            "Waiting for the scanner’s first price update.", "No trades waiting.",
            "Nothing yet. The bot records proposals and why signals are skipped.");
    }

    public CfdDashboardDto Cfd(CfdStatusDto state, DecisionConfirmationStore confirmations, DateTime nowUtc) => new(
        state.Enabled,
        state.Enabled
            ? $"Enabled practice instruments: {string.Join(", ", state.Instruments)}."
            : "No CFDs are enabled. Set CFD_INSTRUMENTS on the server to AU200_AUD and/or SPX500_USD. This module is locked to the OANDA practice endpoint.",
        Money(state.Equity, state.Currency), Money(state.DayPnl, state.Currency), state.Reason,
        state.Pending.Count > 0, state.Pending.Select(p => Proposal(p,
            confirmations.IsArmed("cfd", p.Id, nowUtc))).ToArray(), "No CFD trades are waiting for approval.");

    public DashboardActionResult Action(DecisionResult result, string action) =>
        new(result.Executed || action == "reject", result.Executed, false, result.Message);

    public DashboardActionResult CfdAction(CfdDecisionResult result, string action) =>
        new(result.Executed || action == "reject", result.Executed, false, result.Message);

    private MarketCheckView Instrument(InstrumentScanDto item) => new(
        item.Instrument.Replace('_', '/'), Price(item.Bid, 5), Price(item.Ask, 5), item.Explanation,
        item.PriceTimeUtc is { } priceTime ? LocalTime(priceTime) : "not available",
        item.ReasonUpdatedUtc is { } updated ? LocalTime(updated) : "not yet checked");

    private ForexProposalView Proposal(TradeProposal p, string? currency, bool confirmationArmed) => new(
        p.Id, p.Instrument, p.Side.ToString().ToUpperInvariant(), SideClass(p.Side.ToString()), confirmationArmed,
        $"{p.Units.ToString("N0", DisplayCulture)} units ({(p.Units / 100000m).ToString("0.00", DisplayCulture)} lot)",
        Price(p.EntryPrice, 5), $"{Price(p.StopLoss, 5)} ({p.StopLossPips.ToString("0.0", DisplayCulture)} pips)",
        Price(p.TakeProfit, 5), Money(p.RiskUsd, currency), LocalTime(p.CreatedUtc + ExecutionAgent.ProposalTtl), p.Rationale);

    private CfdProposalView Proposal(CfdPendingDto p, bool confirmationArmed) => new(
        p.Id, p.Instrument, p.Side.ToUpperInvariant(), SideClass(p.Side), confirmationArmed,
        p.Units.ToString("N0", DisplayCulture), Price(p.Entry, p.PricePrecision) + " " + p.Currency,
        $"{Price(p.StopLoss, p.PricePrecision)} ({p.StopDistancePoints.ToString("0.0", DisplayCulture)} points)",
        Price(p.TakeProfit, p.PricePrecision), Money(p.MaxLoss, p.Currency), LocalTime(p.ExpiresUtc), p.Rationale);

    private static string SideClass(string side) => side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? "buy" : "sell";
    private static string Price(decimal? price, int precision) => price is { } value
        ? value.ToString($"F{precision}", DisplayCulture) : "—";
    private static string Money(decimal? value, string? currency) => value is { } amount
        ? $"{currency ?? "USD"} {amount.ToString("N2", DisplayCulture)}" : "—";
    private static string SignedMoney(decimal? value, string? currency) => value is { } amount
        ? $"{(amount >= 0 ? "+" : "-")}{currency ?? "USD"} {Math.Abs(amount).ToString("N2", DisplayCulture)}" : "—";
    private string LocalTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _displayZone)
            .ToString("h:mm:ss tt", DisplayCulture);
}
