using System.Net.Http.Json;
using System.Text.Json;

namespace ForexHitl;

public interface IStateStore
{
    Task<T?> LoadAsync<T>(string key, CancellationToken ct) where T : class;
    Task SaveAsync<T>(string key, T value, CancellationToken ct) where T : class;
}

/// <summary>Local development / persistent-disk fallback. Invalid state is an error, never a reset.</summary>
public sealed class FileStateStore(string? guardPath) : IStateStore
{
    private string? PathFor(string key) => guardPath is null ? null : key == "guard" ? guardPath : guardPath + "." + key;

    public async Task<T?> LoadAsync<T>(string key, CancellationToken ct) where T : class
    {
        var path = PathFor(key);
        if (path is null || !File.Exists(path)) return null;
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
            ?? throw new InvalidDataException("Stored state was null.");
    }

    public async Task SaveAsync<T>(string key, T value, CancellationToken ct) where T : class
    {
        var path = PathFor(key);
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Server-only PostgREST access; a failed request never falls back to an empty local file.</summary>
public sealed class SupabaseStateStore : IStateStore
{
    private readonly HttpClient _http;
    private readonly string _scope;

    public SupabaseStateStore(HttpClient http, AppConfig config)
    {
        _http = http;
        _http.BaseAddress = new Uri(config.SupabaseUrl!.TrimEnd('/') + "/rest/v1/");
        _http.DefaultRequestHeaders.Add("apikey", config.SupabaseKey);
        // New sb_secret keys are opaque, not JWTs. Only legacy service-role JWTs use Bearer.
        if (!config.SupabaseKey!.StartsWith("sb_secret_", StringComparison.Ordinal))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", config.SupabaseKey);
        _scope = $"{(config.UseLive ? "live" : "practice")}:{config.OandaAccountId}:";
    }

    private sealed record Row<T>(string id, T payload);

    public async Task<T?> LoadAsync<T>(string key, CancellationToken ct) where T : class
    {
        var id = Uri.EscapeDataString(_scope + key);
        using var response = await _http.GetAsync($"forex_state?id=eq.{id}&select=id,payload&limit=1", ct);
        Check(response);
        var rows = await response.Content.ReadFromJsonAsync<List<Row<T>>>(cancellationToken: ct)
            ?? throw new InvalidDataException("Supabase returned null state rows.");
        if (rows.Count == 0) return null;
        return rows[0].payload ?? throw new InvalidDataException("Supabase state payload was null.");
    }

    public async Task SaveAsync<T>(string key, T value, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "forex_state?on_conflict=id")
        {
            Content = JsonContent.Create(new Row<T>(_scope + key, value))
        };
        request.Headers.Add("Prefer", "resolution=merge-duplicates,return=minimal");
        using var response = await _http.SendAsync(request, ct);
        Check(response);
    }

    private static void Check(HttpResponseMessage response)
    {
        // Do not relay response bodies, credentials or request URLs to the UI/activity feed.
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Supabase state request failed (HTTP {(int)response.StatusCode}). Check server configuration and the forex_state table.");
    }
}
