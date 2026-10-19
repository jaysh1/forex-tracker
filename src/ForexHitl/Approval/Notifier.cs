using System.Text;

namespace ForexHitl;

/// <summary>Best-effort "something needs you" push. Implementations must never throw.</summary>
public interface IApprovalNotifier
{
    Task NotifyAsync(string title, string message, bool urgent, CancellationToken ct);
}

public sealed class NullNotifier : IApprovalNotifier
{
    public Task NotifyAsync(string title, string message, bool urgent, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Optional push via ntfy (https://ntfy.sh or self-hosted). Install the ntfy app, subscribe to your topic,
/// and a tap on the notification opens the approval page. The topic name is the only secret protecting the
/// feed, so it must be long and random. Titles must be plain ASCII (they travel in an HTTP header).
/// </summary>
public sealed class NtfyNotifier : IApprovalNotifier
{
    private readonly HttpClient _http;
    private readonly AppConfig _config;
    private readonly ILogger<NtfyNotifier> _log;

    public NtfyNotifier(HttpClient http, AppConfig config, ILogger<NtfyNotifier> log)
    {
        _http = http;
        _config = config;
        _log = log;
    }

    public async Task NotifyAsync(string title, string message, bool urgent, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_config.NtfyServer}/{_config.NtfyTopic}")
            {
                Content = new StringContent(message, Encoding.UTF8, "text/plain")
            };
            req.Headers.Add("Title", title);
            req.Headers.Add("Priority", urgent ? "high" : "default");
            if (_config.PublicBaseUrl is { } url) req.Headers.Add("Click", url);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("ntfy returned HTTP {Status}", (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("ntfy notification failed: {Message}", ex.Message);
        }
    }
}
