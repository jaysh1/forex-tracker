namespace ForexHitl;

public sealed record ActivityEntry(DateTime TimeUtc, string Kind, string Text);

/// <summary>
/// Small feed of what the bot did (and why it did not), newest first. Kept in memory; <see cref="StateSyncService"/>
/// saves it to the state store and restores it on startup, so a restart does not wipe it.
/// </summary>
public sealed class ActivityLog
{
    private const int Max = 50;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<ActivityEntry> _items = new();
    private long _version;

    public ActivityLog(TimeProvider time) => _time = time;

    /// <summary>Increases with every change; lets the saver skip work when nothing happened.</summary>
    public long Version
    {
        get { lock (_gate) return _version; }
    }

    public void Add(string kind, string text)
    {
        lock (_gate)
        {
            _items.Insert(0, new ActivityEntry(_time.GetUtcNow().UtcDateTime, kind, text));
            if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
            _version++;
        }
    }

    /// <summary>Merges older entries loaded from storage in behind whatever was logged since startup.</summary>
    public void Restore(IEnumerable<ActivityEntry> saved)
    {
        lock (_gate)
        {
            var merged = _items.Concat(saved).OrderByDescending(e => e.TimeUtc).Take(Max).ToList();
            _items.Clear();
            _items.AddRange(merged);
            _version++;
        }
    }

    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }
}
