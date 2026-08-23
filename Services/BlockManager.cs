using System.Collections.Concurrent;

namespace Pinguin.Services;

/// <summary>
/// Tracks one-way blocks between connected users.
///
/// Blocks are session-scoped: they exist only while both users are connected and are
/// wiped the moment either disconnects, matching the zero-persistence model (a freed
/// username must not inherit the blocks of whoever held it before).
///
/// Two indexes are kept in sync so the question asked on every broadcast --
/// "who has blocked this sender?" -- never scans the whole user set.
/// </summary>
public class BlockManager
{
    // blocker -> everyone they have blocked
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _blocked = new(StringComparer.Ordinal);

    // target -> everyone who has blocked them (reverse index, read on the hot broadcast path)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _blockedBy = new(StringComparer.Ordinal);

    // Writes are rare; a single gate keeps the two indexes consistent. Reads never take it.
    private readonly object _writeGate = new();

    /// <summary>Records that <paramref name="blocker"/> has blocked <paramref name="target"/>.</summary>
    /// <returns>False if it was a self-block or the block already existed.</returns>
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

    /// <returns>False if no such block existed.</returns>
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

    /// <summary>True if either user has blocked the other. DMs are dead in both directions once one side blocks.</summary>
    public bool IsBlockedEitherWay(string a, string b)
    {
        return HasBlocked(a, b) || HasBlocked(b, a);
    }

    /// <summary>
    /// Everyone who has blocked <paramref name="username"/>. Enumerated lazily and lock-free:
    /// the common case (nobody has blocked them) allocates nothing.
    /// </summary>
    public IEnumerable<string> GetBlockersOf(string username)
    {
        if (!_blockedBy.TryGetValue(username, out var reverse)) yield break;
        foreach (var entry in reverse) yield return entry.Key;
    }

    /// <summary>Everyone <paramref name="username"/> has blocked, for restoring their own UI state.</summary>
    public IEnumerable<string> GetBlockedBy(string username)
    {
        if (!_blocked.TryGetValue(username, out var forward)) return Array.Empty<string>();
        return forward.Keys;
    }

    /// <summary>
    /// Erases every trace of a user on disconnect -- both the blocks they made and the
    /// blocks made against them -- so the freed username starts clean.
    /// </summary>
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
