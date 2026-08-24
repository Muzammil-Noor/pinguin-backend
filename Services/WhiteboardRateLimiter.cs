using System.Collections.Concurrent;

namespace Pinguin.Services;

public class WhiteboardRateLimiter
{
    private const int MaxCommits = 50;
    private const int CommitWindowSeconds = 10;

    private const int MaxLiveBatches = 240;
    private const int LiveWindowSeconds = 4;

    private readonly ConcurrentDictionary<string, Queue<DateTime>> _commits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _live = new(StringComparer.Ordinal);

    public bool TryCommit(string connectionId)
        => TryConsume(_commits, connectionId, MaxCommits, CommitWindowSeconds);

    public bool TryLiveBatch(string connectionId)
        => TryConsume(_live, connectionId, MaxLiveBatches, LiveWindowSeconds);

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

    public int GetCommitResetSeconds(string connectionId)
    {
        if (!_commits.TryGetValue(connectionId, out var window)) return 0;

        lock (window)
        {
            if (window.Count == 0) return 0;
            var remaining = (int)Math.Ceiling((window.Peek().AddSeconds(CommitWindowSeconds) - DateTime.UtcNow).TotalSeconds);
            return remaining > 0 ? remaining : 0;
        }
    }

    public void RemoveConnection(string connectionId)
    {
        _commits.TryRemove(connectionId, out _);
        _live.TryRemove(connectionId, out _);
    }
}
