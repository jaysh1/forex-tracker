using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace ForexHitl.Client;

public sealed class SessionExpiredException : Exception
{
    public SessionExpiredException() : base("Session expired") { }
}

/// <summary>
/// Talks to the same-origin approval API. The session token lives in localStorage so a phone browser
/// stays signed in for the token's 12 hour lifetime; Sign out revokes it on the server.
/// </summary>
public sealed class ApiClient
{
    private const string TokenKey = "forexhitl.token";

    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private string? _token;

    public ApiClient(HttpClient http, IJSRuntime js)
    {
        _http = http;
        _js = js;
    }

    public bool IsLoggedIn => !string.IsNullOrEmpty(_token);

    public async Task InitAsync()
    {
        try { _token = await _js.InvokeAsync<string?>("localStorage.getItem", TokenKey); }
        catch (JSException) { _token = null; }
    }

    /// <returns>An error message, or null on success.</returns>
    public async Task<string?> LoginAsync(string password)
    {
        using var resp = await _http.PostAsJsonAsync("api/login", new { password });
        if (!resp.IsSuccessStatusCode) return await ReadErrorAsync(resp);

        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        if (body is null || string.IsNullOrEmpty(body.Token)) return "Unexpected response from server.";

        _token = body.Token;
        await _js.InvokeVoidAsync("localStorage.setItem", TokenKey, _token);
        return null;
    }

    public async Task<StateDto> GetStateAsync()
    {
        using var resp = await SendAsync(HttpMethod.Get, "api/state");
        return await resp.Content.ReadFromJsonAsync<StateDto>()
               ?? throw new HttpRequestException("Empty response from server.");
    }

    public async Task<DecisionResult> DecideAsync(string proposalId, string action)
    {
        using var resp = await SendAsync(HttpMethod.Post, $"api/proposals/{Uri.EscapeDataString(proposalId)}/{action}");
        return await resp.Content.ReadFromJsonAsync<DecisionResult>()
               ?? throw new HttpRequestException("Empty response from server.");
    }

    public async Task LogoutAsync()
    {
        try { using var _ = await SendAsync(HttpMethod.Post, "api/logout"); }
        catch (Exception ex) when (ex is HttpRequestException or SessionExpiredException) { }
        await ClearTokenAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            await ClearTokenAsync();
            throw new SessionExpiredException();
        }

        try { resp.EnsureSuccessStatusCode(); }
        catch { resp.Dispose(); throw; }
        return resp;
    }

    private async Task ClearTokenAsync()
    {
        _token = null;
        try { await _js.InvokeVoidAsync("localStorage.removeItem", TokenKey); }
        catch (JSException) { }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } msg) return msg;
        }
        catch (JsonException) { }
        return $"Login failed (HTTP {(int)resp.StatusCode}).";
    }
}
