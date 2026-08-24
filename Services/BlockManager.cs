using System.Collections.Concurrent;

namespace Pinguin.Services;

public class BlockManager
{
    // blocker -> everyone they have blocked
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _blocked = new(StringComparer.Ordinal);

    // target -> everyone who has blocked them (reverse index, read on the hot broadcast path)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _blockedBy = new(StringComparer.Ordinal);

    // Writes are rare; a single gate keeps the two indexes consistent. Reads never take it.
    private readonly object _writeGate = new();

    public bool Block(string blocker, string target)
    {
        if (string.IsNullOrEmpty(blocker) || string.IsNullOrEmpty(target)) return false;
        if (string.Equals(blocker, target, StringComparison.Ordinal)) return false;

        lock (_writeGate)
        {
            var forward = _blocked.GetOrAdd(blocker, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            if (!forward.TryAdd(target, 0)) return false;

            _blockedBy.GetOrAdd(target, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
                      .TryAdd(blocker, 0);
            return true;
        }
    }

    public bool Unblock(string blocker, string target)
    {
        lock (_writeGate)
        {
            if (!_blocked.TryGetValue(blocker, out var forward) || !forward.TryRemove(target, out _))
            {
                return false;
            }

            if (_blockedBy.TryGetValue(target, out var reverse))
            {
                reverse.TryRemove(blocker, out _);
                if (reverse.IsEmpty) _blockedBy.TryRemove(target, out _);
            }

            if (forward.IsEmpty) _blocked.TryRemove(blocker, out _);
            return true;
        }
    }

    public bool HasBlocked(string blocker, string target)
    {
        return _blocked.TryGetValue(blocker, out var forward) && forward.ContainsKey(target);
    }

    public bool IsBlockedEitherWay(string a, string b)
    {
        return HasBlocked(a, b) || HasBlocked(b, a);
    }

    public IEnumerable<string> GetBlockersOf(string username)
    {
        if (!_blockedBy.TryGetValue(username, out var reverse)) yield break;
        foreach (var entry in reverse) yield return entry.Key;
    }

    public IEnumerable<string> GetBlockedBy(string username)
    {
        if (!_blocked.TryGetValue(username, out var forward)) return Array.Empty<string>();
        return forward.Keys;
    }

    public void RemoveUser(string username)
    {
        lock (_writeGate)
        {
            if (_blocked.TryRemove(username, out var forward))
            {
                foreach (var entry in forward)
                {
                    if (_blockedBy.TryGetValue(entry.Key, out var reverse))
                    {
                        reverse.TryRemove(username, out _);
                        if (reverse.IsEmpty) _blockedBy.TryRemove(entry.Key, out _);
                    }
                }
            }

            if (_blockedBy.TryRemove(username, out var blockers))
            {
                foreach (var entry in blockers)
                {
                    if (_blocked.TryGetValue(entry.Key, out var theirList))
                    {
                        theirList.TryRemove(username, out _);
                        if (theirList.IsEmpty) _blocked.TryRemove(entry.Key, out _);
                    }
                }
            }
        }
    }
}
