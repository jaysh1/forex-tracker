using CFDEngine;

namespace ForexHitl;

/// <summary>Translates the host's economic calendar into CFD-owned news contracts at the composition root.</summary>
public sealed class CfdNewsCalendarAdapter(INewsCalendar calendar) : ICfdNewsCalendar
{
    public async Task<IReadOnlyList<CfdNewsEvent>?> GetEventsAsync(CancellationToken cancellationToken)
    {
        var events = await calendar.GetEventsAsync(cancellationToken);
        return events?.Select(e => new CfdNewsEvent(e.TimeUtc, e.Currency, e.Title, e.Impact)).ToArray();
    }
}
