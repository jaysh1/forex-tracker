using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ForexHitl;

public sealed record LoginResult(bool Ok, string? Token, string? Error, bool Locked);

/// <summary>
/// Single-owner authentication for the approval UI: one password from the environment, random
/// bearer-token sessions held in memory (a restart signs everyone out), and a global lockout after
/// repeated failures. The lockout can be abused to keep the owner out, but that only means trades are
/// not approved - the failure mode is safe.
/// </summary>
public sealed class SessionAuth
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    public const int MaxFailures = 5;

    private readonly byte[] _password;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private readonly object _gate = new();
    private int _failures;
    private DateTime _lockedUntil = DateTime.MinValue;

    public SessionAuth(AppConfig config, TimeProvider time)
        : this(config.ApproverPassword, time) { }

    public SessionAuth(string password, TimeProvider time)
    {
        _password = Encoding.UTF8.GetBytes(password);
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public LoginResult Login(string? password)
    {
        lock (_gate)
        {
            var now = Now;
            if (now < _lockedUntil)
                return new LoginResult(false, null, "Too many failed attempts. Try again later.", Locked: true);

            var supplied = Encoding.UTF8.GetBytes(password ?? "");
            if (!CryptographicOperations.FixedTimeEquals(_password, supplied))
            {
                if (++_failures >= MaxFailures)
                {
                    _failures = 0;
                    _lockedUntil = now + Lockout;
                    return new LoginResult(false, null, "Too many failed attempts. Try again later.", Locked: true);
                }
                return new LoginResult(false, null, "Wrong password.", Locked: false);
            }

            _failures = 0;
            foreach (var (token, expiry) in _sessions)
                if (expiry <= now) _sessions.TryRemove(token, out _);

            var newToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            _sessions[newToken] = now + SessionLifetime;
            return new LoginResult(true, newToken, null, Locked: false);
        }
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var expiry)) return false;
        if (expiry > Now) return true;
        _sessions.TryRemove(token, out _);
        return false;
    }

    public void Logout(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }
}
