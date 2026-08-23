using System.Collections.Concurrent;

namespace Pinguin.Services;

public class UserManager
{
    // ConnectionId -> Username
    private readonly ConcurrentDictionary<string, string> _byConnection = new(StringComparer.Ordinal);

    // Username -> ConnectionId. Doubles as the atomic claim on a username and keeps
    // GetConnectionId O(1) -- it is hit once per blocker on every broadcast.
    private readonly ConcurrentDictionary<string, string> _byUsername = new(StringComparer.Ordinal);

    public bool TryAddUser(string connectionId, string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;

        // Usernames are case-sensitive (PRD 3.1). Whoever wins this TryAdd owns the name --
        // a check-then-add would let two simultaneous joins both claim it.
        if (!_byUsername.TryAdd(username, connectionId)) return false;

        if (!_byConnection.TryAdd(connectionId, username))
        {
            _byUsername.TryRemove(new KeyValuePair<string, string>(username, connectionId));
            return false;
        }

        return true;
    }

    public string? RemoveUser(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var username)) return null;

        // Only drop the name if this connection still owns it, so a reclaim by a
        // new connection isn't undone by the old one's teardown.
        _byUsername.TryRemove(new KeyValuePair<string, string>(username, connectionId));
        return username;
    }

    public string? GetUsername(string connectionId)
    {
        _byConnection.TryGetValue(connectionId, out var username);
        return username;
    }

    public string? GetConnectionId(string username)
    {
        _byUsername.TryGetValue(username, out var connectionId);
        return connectionId;
    }

    public bool IsOnline(string username) => _byUsername.ContainsKey(username);

    public IEnumerable<string> GetAllUsers()
    {
        return _byConnection.Values.ToList();
    }

    public IReadOnlyDictionary<string, string> GetAllUsersDict()
    {
        return new Dictionary<string, string>(_byConnection);
    }
}
