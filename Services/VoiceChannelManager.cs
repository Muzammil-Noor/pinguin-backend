using System.Collections.Concurrent;

namespace Pinguin.Services;

public class VoiceChannelManager
{
    // A full mesh costs every participant N-1 peer connections, so the voice channel caps
    // well below the room's 45. Past roughly a dozen the browsers, not the server, fall over.
    public const int MaxParticipants = 10;

    private sealed class VoiceChannel
    {
        public readonly ConcurrentDictionary<string, byte> Participants = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, byte> Speaking = new(StringComparer.Ordinal);
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<string, VoiceChannel> _channels = new(StringComparer.Ordinal);

    public enum JoinResult { Ok, Full, AlreadyJoined }

    public JoinResult TryJoin(string roomId, string username, out List<string> existing, out List<string> speaking)
    {
        existing = new List<string>();
        speaking = new List<string>();

        var channel = _channels.GetOrAdd(roomId, _ => new VoiceChannel());

        lock (channel.Gate)
        {
            if (channel.Participants.ContainsKey(username)) return JoinResult.AlreadyJoined;
            if (channel.Participants.Count >= MaxParticipants) return JoinResult.Full;

            existing = channel.Participants.Keys.ToList();
            speaking = channel.Speaking.Keys.ToList();
            channel.Participants[username] = 0;

            return JoinResult.Ok;
        }
    }

    public bool Leave(string roomId, string username)
    {
        if (!_channels.TryGetValue(roomId, out var channel)) return false;

        lock (channel.Gate)
        {
            if (!channel.Participants.TryRemove(username, out _)) return false;
            channel.Speaking.TryRemove(username, out _);

            if (channel.Participants.IsEmpty) _channels.TryRemove(roomId, out _);
            return true;
        }
    }

    public bool IsInChannel(string roomId, string username)
    {
        return _channels.TryGetValue(roomId, out var channel) && channel.Participants.ContainsKey(username);
    }

    public List<string> GetParticipants(string roomId)
    {
        return _channels.TryGetValue(roomId, out var channel)
            ? channel.Participants.Keys.ToList()
            : new List<string>();
    }

    public int GetParticipantCount(string roomId)
    {
        return _channels.TryGetValue(roomId, out var channel) ? channel.Participants.Count : 0;
    }

    // Tracked so someone joining mid-transmission sees who is already talking.
    public bool SetSpeaking(string roomId, string username, bool speaking)
    {
        if (!_channels.TryGetValue(roomId, out var channel)) return false;
        if (!channel.Participants.ContainsKey(username)) return false;

        if (speaking) channel.Speaking[username] = 0;
        else channel.Speaking.TryRemove(username, out _);

        return true;
    }

    public List<string> RemoveFromAll(string username)
    {
        var affected = new List<string>();

        foreach (var (roomId, channel) in _channels)
        {
            lock (channel.Gate)
            {
                if (!channel.Participants.TryRemove(username, out _)) continue;
                channel.Speaking.TryRemove(username, out _);
                affected.Add(roomId);

                if (channel.Participants.IsEmpty) _channels.TryRemove(roomId, out _);
            }
        }

        return affected;
    }

    public void RemoveChannel(string roomId)
    {
        _channels.TryRemove(roomId, out _);
    }
}
