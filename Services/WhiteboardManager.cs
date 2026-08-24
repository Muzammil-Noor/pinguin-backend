using System.Collections.Concurrent;
using System.Text.Json;

namespace Pinguin.Services;


public class WhiteboardOp
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public long Seq { get; init; }
    public string Author { get; init; } = string.Empty;
    public JsonElement Payload { get; init; }
    public bool Hidden { get; set; }
}

public class WhiteboardManager
{
    public const int MaxOpsPerRoom = 5000;

    public const int MaxOpPayloadBytes = 64 * 1024;

    private sealed class Whiteboard
    {
        public readonly List<WhiteboardOp> Ops = new();
        public readonly Dictionary<string, List<string>> RedoStacks = new(StringComparer.Ordinal);
        public long NextSeq;
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<string, Whiteboard> _boards = new(StringComparer.Ordinal);

    public enum AddResult { Ok, PayloadTooLarge, BoardFull }

    public AddResult TryAddOp(string roomId, string author, JsonElement payload, out WhiteboardOp? op)
    {
        op = null;

        if (payload.GetRawText().Length > MaxOpPayloadBytes) return AddResult.PayloadTooLarge;

        var board = _boards.GetOrAdd(roomId, _ => new Whiteboard());

        lock (board.Gate)
        {
            if (board.Ops.Count >= MaxOpsPerRoom) return AddResult.BoardFull;

            var committed = new WhiteboardOp
            {
                Seq = board.NextSeq++,
                Author = author,
                // Clone: the JsonDocument backing the hub argument is released once the call returns.
                Payload = payload.Clone()
            };

            board.Ops.Add(committed);
            board.RedoStacks.Remove(author);

            op = committed;
            return AddResult.Ok;
        }
    }

    public string? Undo(string roomId, string author)
    {
        if (!_boards.TryGetValue(roomId, out var board)) return null;

        lock (board.Gate)
        {
            for (var i = board.Ops.Count - 1; i >= 0; i--)
            {
                var candidate = board.Ops[i];
                if (candidate.Hidden || candidate.Author != author) continue;

                candidate.Hidden = true;

                if (!board.RedoStacks.TryGetValue(author, out var stack))
                {
                    stack = new List<string>();
                    board.RedoStacks[author] = stack;
                }
                stack.Add(candidate.Id);

                return candidate.Id;
            }
        }

        return null;
    }

    public string? Redo(string roomId, string author)
    {
        if (!_boards.TryGetValue(roomId, out var board)) return null;

        lock (board.Gate)
        {
            if (!board.RedoStacks.TryGetValue(author, out var stack) || stack.Count == 0) return null;

            var opId = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) board.RedoStacks.Remove(author);

            var op = board.Ops.FirstOrDefault(o => o.Id == opId);
            if (op == null) return null;

            op.Hidden = false;
            return op.Id;
        }
    }

    public void Clear(string roomId)
    {
        if (!_boards.TryGetValue(roomId, out var board)) return;

        lock (board.Gate)
        {
            board.Ops.Clear();
            board.RedoStacks.Clear();
            board.NextSeq = 0;
        }
    }

    public IReadOnlyList<WhiteboardOp> GetState(string roomId)
    {
        if (!_boards.TryGetValue(roomId, out var board)) return Array.Empty<WhiteboardOp>();

        lock (board.Gate)
        {
            return board.Ops.ToList();
        }
    }

    public int GetOpCount(string roomId)
    {
        if (!_boards.TryGetValue(roomId, out var board)) return 0;
        lock (board.Gate) return board.Ops.Count;
    }

    public void RemoveUser(string roomId, string username)
    {
        if (!_boards.TryGetValue(roomId, out var board)) return;
        lock (board.Gate) board.RedoStacks.Remove(username);
    }

    public void RemoveBoard(string roomId)
    {
        _boards.TryRemove(roomId, out _);
    }
}
