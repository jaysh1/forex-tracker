using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForexHitl;

public static class StateKeys
{
    public const string Guard = "guard";
    public const string Activity = "activity";
}

public sealed class StateStoreException(string message) : Exception(message);

/// <summary>
/// Where the bot keeps the little state that must survive restarts: the daily-loss baseline and lockout,
/// and the recent activity feed. Values are JSON text under a short key.
/// </summary>
public interface IStateStore
{
    /// <summary>Shown in messages, e.g. "Supabase".</summary>
    string Name { get; }

    /// <summary>The stored JSON, or null when nothing has been saved under this key yet. Throws if the store cannot be read.</summary>
    Task<string?> LoadAsync(string key, CancellationToken ct);

    Task SaveAsync(string key, string json, CancellationToken ct);
}

/// <summary>Local files (the original behaviour). Only durable if the folder is on a persistent disk.</summary>
public sealed class FileStateStore : IStateStore
{
    private readonly string _guardPath;

    public FileStateStore(string guardPath) => _guardPath = guardPath;

    public string Name => "local file";

    public async Task<string?> LoadAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    public async Task SaveAsync(string key, string json, CancellationToken ct)
    {
        var path = PathFor(key);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
    }

    private string PathFor(string key)
    {
        if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Invalid state key.", nameof(key));

        // The guard keeps its historical file name so an existing state file is still picked up.
        if (key == StateKeys.Guard) return _guardPath;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_guardPath))!, key + ".json");
    }
}

/// <summary>
/// Supabase via its REST (PostgREST) API: one table, bot_state(key text primary key, value jsonb, updated_at).
/// Needs the project URL and a SECRET key, so it must only ever run on the server.
/// </summary>
public sealed class SupabaseStateStore : IStateStore
{
    public const string Table = "bot_state";

    private readonly HttpClient _http;

    public SupabaseStateStore(HttpClient http) => _http = http;

    public string Name => "Supabase";

    /// <summary>
    /// New-style keys (sb_secret_...) go on the apikey header only; Supabase rejects them as a Bearer token.
    /// Legacy JWT keys (service_role) need both headers.
    /// </summary>
    public static void Configure(HttpClient http, string url, string secretKey)
    {
        http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(10);
        http.DefaultRequestHeaders.Add("apikey", secretKey);
        if (!secretKey.StartsWith("sb_", StringComparison.Ordinal))
            http.DefaultRequestHeaders.Authorization = new("Bearer", secretKey);
    }

    public async Task<string?> LoadAsync(string key, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"rest/v1/{Table}?select=value&key=eq.{Uri.EscapeDataString(key)}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw Failure("read", resp, body);

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new StateStoreException("Supabase read returned an unexpected response.");
        if (doc.RootElement.GetArrayLength() == 0) return null;

        return doc.RootElement[0].GetProperty("value").GetRawText();
    }

    public async Task SaveAsync(string key, string json, CancellationToken ct)
    {
        var row = new JsonObject
        {
            ["key"] = key,
            ["value"] = JsonNode.Parse(json),
            ["updated_at"] = DateTime.UtcNow.ToString("O")
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"rest/v1/{Table}")
        {
            Content = new StringContent(row.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Prefer", "resolution=merge-duplicates,return=minimal"); // upsert on the primary key

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw Failure("write", resp, await resp.Content.ReadAsStringAsync(ct));
    }

    private static StateStoreException Failure(string action, HttpResponseMessage resp, string body)
    {
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m)) detail = m.GetString();
        }
        catch (JsonException)
        {
        }

        detail = (detail ?? (body.Length > 200 ? body[..200] : body)).TrimEnd('.');
        var hint = (int)resp.StatusCode switch
        {
            401 or 403 => " Check SUPABASE_SECRET_KEY (it must be the secret key, not the publishable/anon key).",
            404 => " Check SUPABASE_URL and that the bot_state table exists (run the SQL from the README).",
            _ => ""
        };
        return new StateStoreException($"Supabase {action} failed (HTTP {(int)resp.StatusCode}): {detail}.{hint}");
    }
}
