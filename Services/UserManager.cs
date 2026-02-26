using System.Collections.Concurrent;

namespace Pinguin.Services;

public class UserManager
{
    // Key: ConnectionId, Value: Username
    private readonly ConcurrentDictionary<string, string> _users = new();

    public bool TryAddUser(string connectionId, string username)
    {
        // Check if username is already taken (case-insensitive for uniqueness check, 
        // though PRD says case-sensitive, usually uniqueness is case-insensitive to prevent impostors. 
        // We'll stick strictly to PRD: "Case-sensitive, Globally unique")
        if (_users.Values.Any(u => u == username))
        {
            return false;
        }

        return _users.TryAdd(connectionId, username);
    }

    public string? RemoveUser(string connectionId)
    {
        _users.TryRemove(connectionId, out var username);
        return username;
    }

    public string? GetUsername(string connectionId)
    {
        _users.TryGetValue(connectionId, out var username);
        return username;
    }

    public string? GetConnectionId(string username)
    {
        return _users.FirstOrDefault(x => x.Value == username).Key;
    }

    public IEnumerable<string> GetAllUsers()
    {
        return _users.Values.ToList();
    }

    public IReadOnlyDictionary<string, string> GetAllUsersDict()
    {
        return new Dictionary<string, string>(_users);
    }
}
