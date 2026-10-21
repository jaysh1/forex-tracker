using CFDEngine;

namespace ForexHitl;

/// <summary>Connects the independent CFD risk-state contract to the host's configured durable store.</summary>
public sealed class CfdRiskStateStoreAdapter(IStateStore store, string accountScopedKey) : ICfdRiskStateStore
{
    public string Name => store.Name;
    public Task<string?> LoadAsync(CancellationToken ct) => store.LoadAsync(accountScopedKey, ct);
    public Task SaveAsync(string json, CancellationToken ct) => store.SaveAsync(accountScopedKey, json, ct);
}
