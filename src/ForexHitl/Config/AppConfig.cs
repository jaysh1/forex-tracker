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
            get("GUARD_STATE_PATH") is { Length: > 0 } p ? p : "data/guard-state.json");
    }
}
