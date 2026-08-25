using System.Collections.Concurrent;

namespace Pinguin.Services;

public class VoiceRateLimiter
{
    // Joining a mesh is inherently bursty: one offer, one answer and a dozen or more ICE
    // candidates per peer, all within a second or two. The ceiling has to clear a full
    // 9-peer negotiation without letting a client flood the relay.
    private const int MaxSignals = 400;
    private const int SignalWindowSeconds = 10;

    private const int MaxPushToTalk = 40;
    private const int PushToTalkWindowSeconds = 10;

    private readonly ConcurrentDictionary<string, Queue<DateTime>> _signals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _pushToTalk = new(StringComparer.Ordinal);

    public bool TrySignal(string connectionId)
        => TryConsume(_signals, connectionId, MaxSignals, SignalWindowSeconds);

    public bool TryPushToTalk(string connectionId)
        => TryConsume(_pushToTalk, connectionId, MaxPushToTalk, PushToTalkWindowSeconds);

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
        _signals.TryRemove(connectionId, out _);
        _pushToTalk.TryRemove(connectionId, out _);
    }
}
