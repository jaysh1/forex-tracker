namespace ForexHitl;

public sealed record ActivityEntry(DateTime TimeUtc, string Kind, string Text);

/// <summary>Bounded recent feed, optionally saved and restored by ActivityPersistence.</summary>
public sealed class ActivityLog
{
    private const int Max = 50;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<ActivityEntry> _items = new();
    private long _version;
    public long Version { get { lock (_gate) return _version; } }

    public ActivityLog(TimeProvider time) => _time = time;

    public void Add(string kind, string text)
    {
        lock (_gate)
        {
            _items.Insert(0, new ActivityEntry(_time.GetUtcNow().UtcDateTime, kind, text));
            if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
            _version++;
        }
    }

    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }

    public void Restore(IEnumerable<ActivityEntry> saved)
    {
        lock (_gate)
        {
            // Keep activity generated while the asynchronous restore was in flight.
            var merged = _items.Concat(saved).Distinct().OrderByDescending(a => a.TimeUtc).Take(Max).ToList();
            _items.Clear();
            _items.AddRange(merged);
            _version++;
        }
    }
}
