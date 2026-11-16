using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Printman.Server;

/// <summary>Bounded, expiring browser sessions and a process-wide PIN attempt budget.</summary>
public sealed class WebSessionSecurity(string pin, TimeProvider? clock = null)
{
    private readonly byte[] _pinHash = SHA256.HashData(Encoding.UTF8.GetBytes(pin));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private DateTimeOffset _attemptWindow;
    private int _attempts;
    public static TimeSpan SessionLifetime => TimeSpan.FromHours(8);

    public bool VerifyPin(string? candidate)
    {
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            if (now - _attemptWindow >= TimeSpan.FromMinutes(1))
            {
                _attemptWindow = now;
                _attempts = 0;
            }
            if (_attempts >= 20) return false;
            bool matches = candidate != null && candidate.Length <= 128 &&
                CryptographicOperations.FixedTimeEquals(_pinHash,
                    SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Trim())));
            if (!matches) _attempts++;
            return matches;
        }
    }

    public string CreateSession()
    {
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            foreach (var token in _sessions.Where(s => s.Value <= now).Select(s => s.Key).ToArray())
                _sessions.Remove(token);
            if (_sessions.Count >= 256) _sessions.Remove(_sessions.MinBy(s => s.Value).Key);
            var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _sessions.Add(session, now + SessionLifetime);
            return session;
        }
    }

    public bool IsAuthenticated(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Printer-Auth", out var header) && IsSessionValid(header.ToString())) return true;
        if (request.Cookies.TryGetValue("printman_auth", out var cookie) && IsSessionValid(cookie)) return true;
        return request.Headers.TryGetValue("X-Printer-Pin", out var pin) && VerifyPin(pin.ToString());
    }

    private bool IsSessionValid(string token)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(token, out var expires)) return false;
            if (expires > _clock.GetUtcNow()) return true;
            _sessions.Remove(token);
            return false;
        }
    }

    public static bool IsSafeOrigin(HttpRequest request)
    {
        bool customHeader = request.Headers["X-Requested-With"] == "Printman" ||
            request.Headers.ContainsKey("X-Printer-Pin") || request.Headers.ContainsKey("X-Printer-Auth");
        if (!customHeader) return false;
        if (!Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var expected)) return false;
        foreach (var name in new[] { "Origin", "Referer" })
        {
            if (!request.Headers.TryGetValue(name, out var value)) continue;
            if (!Uri.TryCreate(value.ToString(), UriKind.Absolute, out var origin) ||
                !string.Equals(origin.GetLeftPart(UriPartial.Authority),
                    expected.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
