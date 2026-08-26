using System.Collections.Concurrent;

namespace Pinguin.Services;

public class MessageRateLimiter
{
    // PRD 12: messages 10 per 5 seconds, file share signals 5 per minute. One message budget
    // shared across global, DM, room and study room sends -- otherwise a spammer just
    // round-robins the channels.
    private const int MaxMessages = 10;
    private const int MessageWindowSeconds = 5;

    private const int MaxFileSignals = 5;
    private const int FileWindowSeconds = 60;

    private readonly ConcurrentDictionary<string, Queue<DateTime>> _messages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _files = new(StringComparer.Ordinal);

    public bool TryMessage(string connectionId)
        => TryConsume(_messages, connectionId, MaxMessages, MessageWindowSeconds);

    public bool TryFile(string connectionId)
        => TryConsume(_files, connectionId, MaxFileSignals, FileWindowSeconds);

    public int GetMessageResetSeconds(string connectionId)
        => GetResetSeconds(_messages, connectionId, MessageWindowSeconds);

    public int GetFileResetSeconds(string connectionId)
        => GetResetSeconds(_files, connectionId, FileWindowSeconds);

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

    private static int GetResetSeconds(
        ConcurrentDictionary<string, Queue<DateTime>> buckets,
        string key,
        int windowSeconds)
    {
        if (!buckets.TryGetValue(key, out var window)) return 0;

        lock (window)
        {
            if (window.Count == 0) return 0;
            var remaining = (int)Math.Ceiling((window.Peek().AddSeconds(windowSeconds) - DateTime.UtcNow).TotalSeconds);
            return remaining > 0 ? remaining : 0;
        }
    }

    public void RemoveConnection(string connectionId)
    {
        _messages.TryRemove(connectionId, out _);
        _files.TryRemove(connectionId, out _);
    }
}
