using System.Collections.Concurrent;

namespace Pinguin.Services;

/// <summary>
/// Enforces AI rate limits per study room: 6 prompts per 240 seconds.
/// Enforced server-side to prevent abuse of LLM resources.
/// </summary>
public class StudyRoomRateLimiter
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _promptTimestamps = new();
    private const int MaxPrompts = 6;
    private const int WindowSeconds = 240;

    /// <summary>
    /// Attempts to consume a prompt token for the given study room.
    /// </summary>
    /// <returns>True if allowed, false if rate limited.</returns>
    public bool TryConsume(string roomId)
    {
        var timestamps = _promptTimestamps.GetOrAdd(roomId, _ => new ConcurrentQueue<DateTime>());
        
        lock (timestamps)
        {
            var now = DateTime.UtcNow;
            var windowStart = now.AddSeconds(-WindowSeconds);

            // Clean up old timestamps
            while (timestamps.TryPeek(out var timestamp) && timestamp < windowStart)
            {
                timestamps.TryDequeue(out _);
            }

            if (timestamps.Count < MaxPrompts)
            {
                timestamps.Enqueue(now);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Gets the remaining time in seconds until the rate limit resets for the room.
    /// </summary>
    public int GetResetTimeSeconds(string roomId)
    {
        if (_promptTimestamps.TryGetValue(roomId, out var timestamps))
        {
            lock (timestamps)
            {
                if (timestamps.TryPeek(out var oldest))
                {
                    var resetTime = oldest.AddSeconds(WindowSeconds);
                    var remaining = (int)(resetTime - DateTime.UtcNow).TotalSeconds;
                    return remaining > 0 ? remaining : 0;
                }
            }
        }
        return 0;
    }

    public void ClearRoom(string roomId)
    {
        _promptTimestamps.TryRemove(roomId, out _);
    }
}
