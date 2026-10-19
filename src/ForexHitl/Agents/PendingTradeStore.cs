using System.Collections.Concurrent;

namespace ForexHitl;

/// <summary>Thread-safe store of proposals awaiting human approval. TryTake gives exactly-once handling.</summary>
public sealed class PendingTradeStore
{
    private readonly ConcurrentDictionary<string, TradeProposal> _items = new();

    public bool Add(TradeProposal proposal) => _items.TryAdd(proposal.Id, proposal);

    public bool TryTake(string id, out TradeProposal proposal) => _items.TryRemove(id, out proposal!);

    public bool HasPending(string instrument) => _items.Values.Any(p => p.Instrument == instrument);

    public IReadOnlyList<TradeProposal> Snapshot() =>
        _items.Values.OrderBy(p => p.CreatedUtc).ToList();

    public IReadOnlyList<TradeProposal> TakeExpired(DateTime createdBeforeUtc)
    {
        var expired = new List<TradeProposal>();
        foreach (var (id, proposal) in _items)
            if (proposal.CreatedUtc < createdBeforeUtc && _items.TryRemove(id, out var removed))
                expired.Add(removed);
        return expired;
    }
}
