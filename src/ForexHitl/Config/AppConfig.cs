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
    string GuardStatePath)
{
    public const int MinPasswordLength = 12;
    public const int MinNtfyTopicLength = 16;
    public TradingSchedule Schedule { get; init; } = new();
    public string? SupabaseUrl { get; init; }
    public string? SupabaseKey { get; init; }

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

        var supabaseUrl = get("SUPABASE_URL")?.Trim();
        var supabaseKey = get("SUPABASE_SECRET_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(supabaseKey)) supabaseKey = get("SUPABASE_SERVICE_ROLE_KEY")?.Trim();
        supabaseUrl = string.IsNullOrWhiteSpace(supabaseUrl) ? null : supabaseUrl;
        supabaseKey = string.IsNullOrWhiteSpace(supabaseKey) ? null : supabaseKey;
        if ((supabaseUrl is null) != (supabaseKey is null))
            throw new InvalidOperationException("Set both SUPABASE_URL and SUPABASE_SECRET_KEY (or legacy SUPABASE_SERVICE_ROLE_KEY).");
        if (supabaseUrl is not null)
        {
            if (!Uri.TryCreate(supabaseUrl, UriKind.Absolute, out var url) || url.Scheme != "https" ||
                url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0 || url.AbsolutePath != "/")
                throw new InvalidOperationException("SUPABASE_URL must be the HTTPS project URL with no path, query or credentials.");
            ValidateSupabaseKey(supabaseKey!);
        }

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
            get("GUARD_STATE_PATH") is { Length: > 0 } p ? p : "data/guard-state.json")
        {
            Schedule = new TradingSchedule(get("TRADING_TIME_ZONE") ?? "Australia/Melbourne",
                get("TRADING_START") ?? "09:00", get("TRADING_END") ?? "17:00",
                get("TRADING_DAYS") ?? "Monday,Tuesday,Wednesday,Thursday,Friday"),
            SupabaseUrl = supabaseUrl,
            SupabaseKey = supabaseKey
        };
    }

    private static void ValidateSupabaseKey(string key)
    {
        if (key.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Supabase key contains whitespace.");
        if (key.StartsWith("sb_secret_", StringComparison.Ordinal) && key.Length > "sb_secret_".Length) return;
        // Reject anon/publishable keys early. Supabase, not this parser, verifies JWT signatures.
        try
        {
            var parts = key.Split('.');
            if (parts.Length != 3) throw new FormatException();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            if (json.RootElement.GetProperty("role").GetString() == "service_role") return;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
        throw new InvalidOperationException("Use a server-side sb_secret_ key or legacy service_role JWT, never an anon/publishable key.");
    }
}
