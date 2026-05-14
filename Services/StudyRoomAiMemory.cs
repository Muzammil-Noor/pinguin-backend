using System.Collections.Concurrent;

namespace Pinguin.Services;

/// <summary>
/// Maintains isolated AI conversation history per study room.
/// Memory lasts the entire 3-hour lifecycle and is cleared when the room is destroyed.
/// </summary>
public class StudyRoomAiMemory
{
    private readonly ConcurrentDictionary<string, List<AiMessage>> _memory = new();

    public void AddMessage(string roomId, string role, string content)
    {
        var history = _memory.GetOrAdd(roomId, _ => new List<AiMessage>());
        lock (history)
        {
            history.Add(new AiMessage { Role = role, Content = content });
        }
    }

    public List<AiMessage> GetHistory(string roomId)
    {
        if (_memory.TryGetValue(roomId, out var history))
        {
            lock (history)
            {
                return new List<AiMessage>(history);
            }
        }
        return new List<AiMessage>();
    }

    public void ClearRoom(string roomId)
    {
        _memory.TryRemove(roomId, out _);
    }
}
