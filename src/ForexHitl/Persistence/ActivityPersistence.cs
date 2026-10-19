namespace ForexHitl;

/// <summary>The recent feed is best effort: save changes every 20s and flush on graceful shutdown.</summary>
public sealed class ActivityPersistence(IStateStore store, ActivityLog activity, ILogger<ActivityPersistence> log)
    : BackgroundService
{
    private bool _loaded;
    private long _savedVersion = -1;

    public async Task SyncAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            var saved = await store.LoadAsync<List<ActivityEntry>>("activity", ct);
            if (saved is not null) activity.Restore(saved);
            _loaded = true;
        }
        var version = activity.Version;
        if (version == _savedVersion) return;
        await store.SaveAsync("activity", activity.Snapshot().ToList(), ct);
        _savedVersion = version;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { log.LogWarning("Activity persistence unavailable; retrying in 20 seconds."); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await SyncAsync(cancellationToken); }
        catch (Exception) { log.LogWarning("Could not flush recent activity at shutdown."); }
    }
}
