using System.Collections.Concurrent;

namespace Banter.App;

/// <summary>
/// Host-agnostic timeline state, and the thread seam between the network and the renderer.
///
/// <para><b>Threading contract.</b> <see cref="BanterClient"/> events arrive on background socket
/// threads while CupriFace reads the model on the render thread. Rather than lock the model — which
/// would still tear a <c>data-repeat</c> mid-bind — every mutation is queued as a closure and run
/// by <see cref="ApplyPending"/> on the render thread, from the app's <c>Present</c> override. The
/// model is therefore only ever touched by one thread, and the queue is the only shared state.</para>
///
/// <para>Nothing here references CupriFace, so the whole of the client's behaviour is testable
/// without a document, a window, or a server.</para>
/// </summary>
public sealed class ChatViewModel
{
    private readonly ConcurrentQueue<Action> _pending = new();

    /// <summary>Streams in flight, so a delta can find the row it is growing.</summary>
    private readonly Dictionary<string, MessageRow> _streams = [];

    /// <summary>Per-room backlog, so switching rooms doesn't lose what was said.</summary>
    private readonly Dictionary<string, List<MessageRow>> _rooms = [];

    public ChatModel Model { get; } = new();

    /// <summary>How many messages a room keeps before the oldest are dropped. Older history is
    /// still on the server and is re-fetchable with <c>HISTORY_REQ</c>.</summary>
    public int RoomScrollback { get; init; } = 5_000;

    /// <summary>Queue a mutation. Safe from any thread; runs later on the render thread.</summary>
    public void Post(Action mutation) => _pending.Enqueue(mutation);

    /// <summary>
    /// Run everything queued. Returns true when at least one mutation ran, so the caller only
    /// pays for a <c>Refresh()</c> when something actually changed.
    /// </summary>
    public bool ApplyPending()
    {
        var any = false;
        while (_pending.TryDequeue(out var mutation))
        {
            mutation();
            any = true;
        }

        return any;
    }

    // ── Mutations. All are called on the render thread, via Post. ────────────────────────────

    public void SetStatus(string text, bool connected)
    {
        Model.Status = text;
        Model.StatusClass = connected ? "status on" : "status off";
    }

    public void SetNick(string nick) => Model.Nick = nick;

    public void AddRoom(string room)
    {
        if (!_rooms.ContainsKey(room))
        {
            _rooms[room] = [];
        }

        if (!Model.Rooms.Any(r => r.Name == room))
        {
            Model.Rooms.Add(new RoomRow { Name = room });
        }

        if (Model.ActiveRoom.Length == 0)
        {
            SwitchTo(room);
        }
    }

    public void RemoveRoom(string room)
    {
        _rooms.Remove(room);
        Model.Rooms.RemoveAll(r => r.Name == room);
        if (Model.ActiveRoom == room)
        {
            Model.ActiveRoom = "";
            Model.Messages.Clear();
            var next = Model.Rooms.FirstOrDefault();
            if (next is not null)
            {
                SwitchTo(next.Name);
            }
        }
    }

    public void SwitchTo(string room)
    {
        if (!_rooms.ContainsKey(room))
        {
            return;
        }

        Model.ActiveRoom = room;
        foreach (var tab in Model.Rooms)
        {
            tab.TabClass = tab.Name == room ? "tab active" : "tab";
            if (tab.Name == room)
            {
                tab.Badge = "";
            }
        }

        // Rebind the visible list to this room's backlog. Replacing the list contents rather
        // than the list itself keeps the binding path (`Messages`) stable.
        Model.Messages.Clear();
        Model.Messages.AddRange(_rooms[room]);
    }

    public void SetTopic(string room, string topic)
    {
        if (room == Model.ActiveRoom)
        {
            Model.Topic = topic;
        }
    }

    public MessageRow Append(string room, string sender, string text, long timestamp, string rowClass = "line")
    {
        var row = new MessageRow
        {
            Sender = sender,
            Text = text,
            Time = FormatTime(timestamp),
            RowClass = sender == Model.Nick && rowClass == "line" ? "line own" : rowClass,
        };

        var backlog = _rooms.TryGetValue(room, out var existing) ? existing : _rooms[room] = [];
        backlog.Add(row);
        if (backlog.Count > RoomScrollback)
        {
            backlog.RemoveAt(0);
        }

        if (room == Model.ActiveRoom)
        {
            Model.Messages.Add(row);
            if (Model.Messages.Count > RoomScrollback)
            {
                Model.Messages.RemoveAt(0);
            }
        }
        else
        {
            var tab = Model.Rooms.FirstOrDefault(r => r.Name == room);
            if (tab is not null && rowClass != "line system")
            {
                tab.Badge = tab.Badge.Length == 0 ? "1" : (int.TryParse(tab.Badge, out var n) ? n + 1 : 1).ToString();
            }
        }

        return row;
    }

    public void System(string room, string text) => Append(room, "*", text, 0, "line system");

    // ── Streaming: START opens an empty row, deltas grow it, END replaces it authoritatively ──

    public void StreamStart(string room, string sender, string streamId)
    {
        var row = Append(room, sender, "", 0, "line streaming");
        _streams[streamId] = row;
    }

    public void StreamDelta(string streamId, string delta)
    {
        if (_streams.TryGetValue(streamId, out var row))
        {
            row.Text += delta;
        }
    }

    /// <summary>
    /// The server's <c>FinalText</c> is authoritative — it replaces the accumulated deltas, so a
    /// dropped delta cannot leave a permanently corrupted message on screen.
    /// </summary>
    public void StreamEnd(string streamId, string finalText, long timestamp)
    {
        if (!_streams.Remove(streamId, out var row))
        {
            return;
        }

        row.Text = finalText;
        row.Time = FormatTime(timestamp);
        row.RowClass = row.RowClass.Replace(" streaming", "");
    }

    /// <summary>Rows currently held for a room — the backlog, not just what is on screen.</summary>
    public IReadOnlyList<MessageRow> Backlog(string room) =>
        _rooms.TryGetValue(room, out var rows) ? rows : [];

    private static string FormatTime(long unixMs) =>
        (unixMs == 0 ? DateTimeOffset.Now : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime())
        .ToString("HH:mm");
}
