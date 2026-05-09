using System.Collections.Concurrent;

namespace Pinguin.Services;

public class Chatroom
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public class ChatroomManager
{
    private readonly ConcurrentDictionary<string, Chatroom> _rooms = new();

    public Chatroom CreateRoom(string name, string ownerUsername)
    {
        var room = new Chatroom
        {
            Name = name,
            Owner = ownerUsername,
            Members = new List<string> { ownerUsername }
        };
        _rooms.TryAdd(room.Id, room);
        return room;
    }

    public bool DeleteRoom(string roomId, string requesterUsername)
    {
        if (_rooms.TryGetValue(roomId, out var room) && room.Owner == requesterUsername)
        {
            return _rooms.TryRemove(roomId, out _);
        }
        return false;
    }

    public bool RenameRoom(string roomId, string newName, string requesterUsername)
    {
        if (_rooms.TryGetValue(roomId, out var room) && room.Owner == requesterUsername)
        {
            room.Name = newName;
            return true;
        }
        return false;
    }

    public bool TryAddMember(string roomId, string username)
    {
        if (_rooms.TryGetValue(roomId, out var room))
        {
            lock (room.Members)
            {
                if (!room.Members.Contains(username))
                {
                    room.Members.Add(username);
                    return true;
                }
            }
        }
        return false;
    }

    public (Chatroom? room, bool wasOwner, bool deleted) RemoveMember(string roomId, string username)
    {
        if (_rooms.TryGetValue(roomId, out var room))
        {
            lock (room.Members)
            {
                if (room.Members.Remove(username))
                {
                    bool wasOwner = false;
                    bool deleted = false;
                    if (room.Owner == username)
                    {
                        wasOwner = true;
                        if (room.Members.Count > 0)
                        {
                            room.Owner = room.Members.First(); // Promote oldest remaining member
                        }
                        else
                        {
                            _rooms.TryRemove(roomId, out _); // Delete if empty
                            deleted = true;
                        }
                    }
                    else if (room.Members.Count == 0)
                    {
                        _rooms.TryRemove(roomId, out _);
                        deleted = true;
                    }
                    return (room, wasOwner, deleted);
                }
            }
        }
        return (null, false, false);
    }

    public bool KickMember(string roomId, string targetUsername, string requesterUsername)
    {
        if (_rooms.TryGetValue(roomId, out var room) && room.Owner == requesterUsername && targetUsername != requesterUsername)
        {
            lock (room.Members)
            {
                return room.Members.Remove(targetUsername);
            }
        }
        return false;
    }

    public Chatroom? GetRoom(string roomId)
    {
        _rooms.TryGetValue(roomId, out var room);
        return room;
    }

    public IEnumerable<Chatroom> GetAllRooms()
    {
        return _rooms.Values.ToList();
    }
}
