using System.Text.Json;

namespace ForexHitl;

/// <summary>
/// Loads the guard's state from the <see cref="IStateStore"/> at startup and saves it whenever it changes.
///
/// Failing safe matters more than failing quietly here:
///  - if the saved state cannot be read, the guard stays "not ready" and trading stays paused, because
///    carrying on with a fresh baseline could silently erase a lockout or the day's starting equity;
///  - if a save fails, trading continues on the in-memory state and the save is retried on every flush.
/// </summary>
public sealed class GuardPersistence
{
    private static readonly TimeSpan RepeatProblemAfter = TimeSpan.FromMinutes(10);

    private readonly DailyRiskGuard _guard;
    private readonly IStateStore _store;
    private readonly ActivityLog _activity;
    private readonly TimeProvider _time;
    private readonly ILogger<GuardPersistence> _log;
    private readonly SemaphoreSlim _io = new(1, 1);
    private string? _lastProblem;
    private DateTime _lastProblemAtUtc = DateTime.MinValue;

    public GuardPersistence(
        DailyRiskGuard guard, IStateStore store, ActivityLog activity, TimeProvider time, ILogger<GuardPersistence> log)
    {
        _guard = guard;
        _store = store;
        _activity = activity;
        _time = time;
        _log = log;
    }

    /// <summary>Loads saved state if that has not succeeded yet. Never throws (except on cancellation).</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_guard.Ready) return;

        await _io.WaitAsync(ct);
        try
        {
            if (_guard.Ready) return;

            try
            {
                var json = await _store.LoadAsync(StateKeys.Guard, ct);
                var saved = json is null ? null : JsonSerializer.Deserialize<GuardState>(json);
                if (json is not null && saved is null) throw new JsonException("saved state was empty");
                if (saved is not null) Validate(saved, _time.GetUtcNow().UtcDateTime);

                _guard.Restore(saved);
                _lastProblem = null;
                _activity.Add("info", saved is null
                    ? $"No saved risk state in {_store.Name} yet - starting fresh."
                    : $"Loaded saved risk state from {_store.Name}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report($"Cannot load the saved risk state from {_store.Name}: {TechnicalAnalyticsAgent.Describe(ex).TrimEnd('.')}. " +
                       "Trading stays paused until it loads.");
            }
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// A saved row that makes no sense (zero baseline, a session start that is not a session boundary or lies in
    /// the future, a lockout with no reason) is not trusted: better to stay paused than to trade on bad numbers.
    /// </summary>
    internal static void Validate(GuardState s, DateTime nowUtc)
    {
        bool bad =
            s.StartEquity <= 0 ||
            s.SessionStartUtc == default || s.SessionStartUtc.Kind != DateTimeKind.Utc ||
            s.SessionStartUtc != DailyRiskGuard.SessionStart(s.SessionStartUtc) ||
            s.SessionStartUtc > DailyRiskGuard.SessionStart(nowUtc) ||
            (s.LockedUntilUtc is { } until && (until.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(s.LockReason)));
        if (bad) throw new JsonException("the saved risk state is invalid (inspect the bot_state table)");
    }

    /// <summary>Saves the guard state if it changed. Never throws (except on cancellation).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            var snapshot = _guard.TakeDirtySnapshot();
            if (snapshot is null) return;

            try
            {
                await _store.SaveAsync(StateKeys.Guard, JsonSerializer.Serialize(snapshot), ct);
                _lastProblem = null;
            }
            catch (OperationCanceledException)
            {
                _guard.MarkDirty();
                throw;
            }
            catch (Exception ex)
            {
                _guard.MarkDirty();
                Report($"Could not save the risk state to {_store.Name}: {TechnicalAnalyticsAgent.Describe(ex).TrimEnd('.')}. " +
                       "Will keep retrying; trading continues on the in-memory state, but a restart now could lose it.");
            }
        }
        finally
        {
            _io.Release();
        }
    }

    private void Report(string message)
    {
        _log.LogWarning("{Message}", message);

        var now = _time.GetUtcNow().UtcDateTime;
        if (message == _lastProblem && now - _lastProblemAtUtc < RepeatProblemAfter) return;

        _lastProblem = message;
        _lastProblemAtUtc = now;
        _activity.Add("error", message.Length > 400 ? message[..400] + "…" : message);
    }
}
