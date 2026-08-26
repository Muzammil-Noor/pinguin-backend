using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Pinguin.Services;

public class ConnectionGuard
{
    // PRD 11 requires per-IP connection limiting; the figure is ours. Generous enough for a
    // shared NAT, low enough to stop a single-host connection flood.
    private const int MaxConnectionsPerIp = 30;
    private const int ConnectionWindowSeconds = 60;

    // PRD 12: username attempts, 5 per minute. Per connection rather than per IP so several
    // people behind one NAT can all pick names; a bot rotating connections to dodge this
    // runs into the per-IP connection cap and pays the join challenge each time.
    private const int MaxUsernameAttempts = 5;
    private const int UsernameWindowSeconds = 60;

    // Proof-of-work join challenge (PRD 11's lightweight CAPTCHA alternative -- a real
    // CAPTCHA service would break the anonymity model). The client must find a suffix whose
    // SHA-256(prefix + suffix) starts with this many zero bits: ~4k hashes on average,
    // a fraction of a second in a browser, pure cost for a bot farm.
    public const int ChallengeDifficultyBits = 12;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);

    private sealed class Challenge
    {
        public string Prefix = string.Empty;
        public DateTime IssuedAt;
    }

    private readonly ConcurrentDictionary<string, Queue<DateTime>> _connectionsByIp = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _usernameAttempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Challenge> _challenges = new(StringComparer.Ordinal);

    public bool AllowConnection(string ip)
        => TryConsume(_connectionsByIp, ip, MaxConnectionsPerIp, ConnectionWindowSeconds);

    public bool AllowUsernameAttempt(string connectionId)
        => TryConsume(_usernameAttempts, connectionId, MaxUsernameAttempts, UsernameWindowSeconds);

    public object IssueChallenge(string connectionId)
    {
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _challenges[connectionId] = new Challenge { Prefix = prefix, IssuedAt = DateTime.UtcNow };
        return new { prefix, difficulty = ChallengeDifficultyBits };
    }

    // One-time use: a failed join (name taken, etc.) consumes the challenge, so every retry
    // costs a fresh proof of work.
    public bool VerifyChallenge(string connectionId, string? solution)
    {
        if (!_challenges.TryRemove(connectionId, out var challenge)) return false;
        if (DateTime.UtcNow - challenge.IssuedAt > ChallengeLifetime) return false;
        if (string.IsNullOrEmpty(solution) || solution.Length > 64) return false;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(challenge.Prefix + solution));
        return CountLeadingZeroBits(hash) >= ChallengeDifficultyBits;
    }

    private static int CountLeadingZeroBits(byte[] hash)
    {
        var bits = 0;
        foreach (var b in hash)
        {
            if (b == 0) { bits += 8; continue; }
            for (var mask = 0x80; mask > 0 && (b & mask) == 0; mask >>= 1) bits++;
            break;
        }
        return bits;
    }

    private static bool TryConsume(
        ConcurrentDictionary<string, Queue<DateTime>> buckets,
        string key,
        int limit,
        int windowSeconds)
    {
        var window = buckets.GetOrAdd(key, _ => new Queue<DateTime>());

        lock (window)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddSeconds(-windowSeconds);

            while (window.Count > 0 && window.Peek() < cutoff) window.Dequeue();

            if (window.Count >= limit) return false;

            window.Enqueue(now);
            return true;
        }
    }

    public void RemoveConnection(string connectionId)
    {
        _challenges.TryRemove(connectionId, out _);
        _usernameAttempts.TryRemove(connectionId, out _);
    }
}
