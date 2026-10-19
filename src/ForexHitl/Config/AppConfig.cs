namespace ForexHitl;

/// <summary>All secrets and account identifiers come from environment variables - never from files.</summary>
public sealed record AppConfig(
    string OandaToken,
    string OandaAccountId,
    bool UseLive,
    string ApproverPassword,
    string? NtfyTopic,
    string NtfyServer,
    string? PublicBaseUrl,
    IReadOnlyList<string> Instruments,
    IReadOnlyList<string> CalendarUrls,
    string GuardStatePath,
    TradingWindow TradingHours,
    string? SupabaseUrl = null,
    string? SupabaseSecretKey = null)
{
    public const int MinPasswordLength = 12;
    public const int MinNtfyTopicLength = 16;

    public string OandaBaseUrl => UseLive
        ? "https://api-fxtrade.oanda.com/"
        : "https://api-fxpractice.oanda.com/";

    public static AppConfig FromEnvironment(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        string Required(string name)
        {
            var v = get(name);
            if (string.IsNullOrWhiteSpace(v))
                throw new InvalidOperationException($"Missing required environment variable {name}.");
            return v.Trim();
        }

        var env = (get("OANDA_ENVIRONMENT") ?? "practice").Trim().ToLowerInvariant();
        if (env is not ("practice" or "live"))
            throw new InvalidOperationException("OANDA_ENVIRONMENT must be 'practice' or 'live'.");

        var live = env == "live";
        if (live && !string.Equals(get("ALLOW_LIVE_TRADING")?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "OANDA_ENVIRONMENT=live also requires ALLOW_LIVE_TRADING=true. Refusing to start.");

        var password = Required("APPROVER_PASSWORD");
        if (password.Length < MinPasswordLength)
            throw new InvalidOperationException($"APPROVER_PASSWORD must be at least {MinPasswordLength} characters.");

        var topic = get("NTFY_TOPIC")?.Trim();
        if (string.IsNullOrEmpty(topic)) topic = null;
        if (topic is not null &&
            (topic.Length < MinNtfyTopicLength || topic.Length > 64 ||
             !topic.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new InvalidOperationException(
                $"NTFY_TOPIC must be {MinNtfyTopicLength}-64 chars of A-Z a-z 0-9 _ - (it acts as a secret; make it long and random).");

        var instruments = (get("INSTRUMENTS") ?? "EUR_USD,GBP_USD")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(i => i.ToUpperInvariant())
            .ToList();
        foreach (var i in instruments) _ = ForexHitl.Instruments.PipSize(i); // throws if unsupported

        var calendarUrls = (get("CALENDAR_FEED_URLS") ??
                "https://nfs.faireconomy.media/ff_calendar_thisweek.json,https://nfs.faireconomy.media/ff_calendar_nextweek.json")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var publicUrl = get("PUBLIC_BASE_URL")?.Trim().TrimEnd('/');
        var ntfyServer = (get("NTFY_SERVER") is { Length: > 0 } s ? s.Trim() : "https://ntfy.sh").TrimEnd('/');

        var tradingHours = TradingWindow.Parse(get("TRADING_HOURS"), get("TRADING_DAYS"), get("TRADING_TIMEZONE"));
        var (supabaseUrl, supabaseKey) = ReadSupabase(get("SUPABASE_URL"), get("SUPABASE_SECRET_KEY"));

        return new AppConfig(
            Required("OANDA_API_TOKEN"),
            Required("OANDA_ACCOUNT_ID"),
            live,
            password,
            topic,
            ntfyServer,
            string.IsNullOrEmpty(publicUrl) ? null : publicUrl,
            instruments,
            calendarUrls,
            get("GUARD_STATE_PATH") is { Length: > 0 } p ? p : "data/guard-state.json",
            tradingHours,
            supabaseUrl,
            supabaseKey);
    }

    private static (string? Url, string? Key) ReadSupabase(string? rawUrl, string? rawKey)
    {
        var url = rawUrl?.Trim();
        var key = rawKey?.Trim();
        if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(key)) return (null, null);

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException("SUPABASE_URL and SUPABASE_SECRET_KEY must both be set, or both left empty.");

        url = url.TrimEnd('/');
        if (url.EndsWith("/rest/v1", StringComparison.OrdinalIgnoreCase)) url = url[..^"/rest/v1".Length];
        // https only, because the secret key travels in the request. The one exception is a Supabase running on
        // this same machine (the Supabase CLI's local stack, http://127.0.0.1:54321), for development.
        var valid = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/" &&
                    (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
        if (!valid)
            throw new InvalidOperationException(
                "SUPABASE_URL must be your project URL over https, like https://abcdefgh.supabase.co");

        if (key.StartsWith("sb_publishable_", StringComparison.Ordinal) || JwtRole(key) == "anon")
            throw new InvalidOperationException(
                "SUPABASE_SECRET_KEY is a publishable/anon key. Use the project's secret key (sb_secret_...) instead.");

        return (url, key);
    }

    /// <summary>The "role" claim of a legacy Supabase JWT key, or null if the key is not a readable JWT.</summary>
    private static string? JwtRole(string key)
    {
        var parts = key.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("role", out var r) ? r.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
