namespace ForexHitl;

/// <summary>Server-owned short-lived second-tap confirmation for trade approvals.</summary>
public sealed class DecisionConfirmationStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinimumTapGap = TimeSpan.FromMilliseconds(800);
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime ExpiresUtc, DateTime FirstTapUtc, string FirstRequestId)> _armed = new(StringComparer.Ordinal);

    public bool TryConfirm(string market, string proposalId, string requestId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return false;
        var key = $"{market}:{proposalId}";
        lock (_gate)
        {
            RemoveExpired(nowUtc);
            if (_armed.TryGetValue(key, out var armed))
            {
                // Idempotent retries of the first POST can never become a second approval tap.
                if (armed.FirstRequestId == requestId || nowUtc - armed.FirstTapUtc < MinimumTapGap) return false;
                _armed.Remove(key);
                return true;
            }

            _armed[key] = (nowUtc + Lifetime, nowUtc, requestId);
            return false;
        }
    }

    public bool IsArmed(string market, string proposalId, DateTime nowUtc)
    {
        lock (_gate)
        {
            RemoveExpired(nowUtc);
            return _armed.TryGetValue($"{market}:{proposalId}", out var armed) && armed.ExpiresUtc > nowUtc;
        }
    }

    public void Clear(string market, string proposalId)
    {
        lock (_gate) _armed.Remove($"{market}:{proposalId}");
    }

    private void RemoveExpired(DateTime nowUtc)
    {
        foreach (var key in _armed.Where(pair => pair.Value.ExpiresUtc <= nowUtc).Select(pair => pair.Key).ToArray())
            _armed.Remove(key);
    }
}
