using System.Text.Json;

namespace ForexHitl;

/// <summary>
/// Startup/shutdown glue for persistence. On start it loads the guard state (before the scanner starts, since
/// hosted services start in registration order) and the saved activity feed. While running it saves the activity
/// feed every few seconds when it changed. On shutdown it makes one last attempt to save everything.
/// The guard state itself is saved by the execution agent the moment it changes.
/// </summary>
public sealed class StateSyncService : BackgroundService
{
    public static readonly TimeSpan ActivitySaveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(8);

    private readonly GuardPersistence _guardPersistence;
    private readonly ActivityLog _activity;
    private readonly IStateStore _store;
    private readonly ILogger<StateSyncService> _log;
    private long _savedVersion = -1;

    public StateSyncService(
        GuardPersistence guardPersistence, ActivityLog activity, IStateStore store, ILogger<StateSyncService> log)
    {
        _guardPersistence = guardPersistence;
        _activity = activity;
        _store = store;
        _log = log;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadActivityAsync(cancellationToken);
        await _guardPersistence.EnsureLoadedAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(ActivitySaveInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await SaveActivityIfChangedAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        using var budget = new CancellationTokenSource(ShutdownBudget);
        try
        {
            await _guardPersistence.FlushAsync(budget.Token);
            await SaveActivityIfChangedAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Shutdown flush of saved state timed out");
        }
    }

    /// <summary>Saves the feed if it changed since the last successful save. Failures are logged, never thrown.</summary>
    public async Task SaveActivityIfChangedAsync(CancellationToken ct)
    {
        var version = _activity.Version;
        if (version == _savedVersion) return;

        try
        {
            await _store.SaveAsync(StateKeys.Activity, JsonSerializer.Serialize(_activity.Snapshot()), ct);
            _savedVersion = version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not added to the activity feed itself: that would change the feed and trigger another save.
            _log.LogWarning("Could not save the activity feed to {Store}: {Message}", _store.Name, TechnicalAnalyticsAgent.Describe(ex));
        }
    }

    private async Task LoadActivityAsync(CancellationToken ct)
    {
        try
        {
            var json = await _store.LoadAsync(StateKeys.Activity, ct);
            if (json is null) return;

            var saved = JsonSerializer.Deserialize<List<ActivityEntry>>(json);
            if (saved is not null) _activity.Restore(saved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The feed is a convenience; unlike the guard state, losing it does not make trading unsafe.
            _log.LogWarning("Could not load the saved activity feed from {Store}: {Message}", _store.Name, TechnicalAnalyticsAgent.Describe(ex));
        }
    }
}
