using System.Collections.Concurrent;

namespace Pinguin.Services;

public class StudyRoomManager
{
    private readonly ConcurrentDictionary<string, StudyRoom> _rooms = new();
    private readonly ConcurrentDictionary<string, PendingStudyRoomInvite> _pendingInvites = new();

    public StudyRoom CreateRoom(string owner, List<string> allMembers)
    {
        var room = new StudyRoom
        {
            Owner = owner,
            Members = new List<string>(allMembers),
            ExpiresAt = DateTime.UtcNow.AddHours(3)
        };
        _rooms.TryAdd(room.Id, room);
        return room;
    }

    public StudyRoom? GetRoom(string roomId)
    {
        _rooms.TryGetValue(roomId, out var room);
        return room;
    }

    public IEnumerable<StudyRoom> GetAllRooms()
    {
        return _rooms.Values.ToList();
    }

    public IEnumerable<StudyRoom> GetRoomsForUser(string username)
    {
        return _rooms.Values.Where(r => r.Members.Contains(username)).ToList();
    }

    public bool IsExpired(string roomId)
    {
        if (_rooms.TryGetValue(roomId, out var room))
        {
            return DateTime.UtcNow >= room.ExpiresAt;
        }
        return true;
    }

    public (StudyRoom? room, bool wasOwner, bool deleted) RemoveMember(string roomId, string username)
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
                            room.Owner = room.Members.First();
                        }
                        else
                        {
                            _rooms.TryRemove(roomId, out _);
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

    public bool DestroyRoom(string roomId)
    {
        return _rooms.TryRemove(roomId, out _);
    }

    /// <summary>
    /// Returns all expired study rooms.
    /// </summary>
    public IEnumerable<StudyRoom> GetExpiredRooms()
    {
        return _rooms.Values
            .Where(r => DateTime.UtcNow >= r.ExpiresAt)
            .ToList();
    }
}
