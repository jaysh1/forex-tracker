namespace ForexHitl;

public sealed record ActivityEntry(DateTime TimeUtc, string Kind, string Text);

/// <summary>Small in-memory feed of what the bot did (and why it did not), newest first. Cleared on restart.</summary>
public sealed class ActivityLog
{
    private const int Max = 50;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<ActivityEntry> _items = new();

    public ActivityLog(TimeProvider time) => _time = time;

    public void Add(string kind, string text)
    {
        lock (_gate)
        {
            _items.Insert(0, new ActivityEntry(_time.GetUtcNow().UtcDateTime, kind, text));
            if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        }
    }

    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }
}
