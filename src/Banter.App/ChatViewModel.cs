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
public sealed partial class ChatViewModel
{
    private readonly ConcurrentQueue<Action> _pending = new();

    /// <summary>Streams in flight, so a delta can find the row it is growing.</summary>
    private readonly Dictionary<string, MessageRow> _streams = [];

    /// <summary>Per-room backlog, so switching rooms doesn't lose what was said.</summary>
    private readonly Dictionary<string, List<MessageRow>> _rooms = [];

    /// <summary>
    /// Per-room paging cursor: the <c>NextCursor</c> from the last history page, i.e. the id to
    /// ask <em>before</em> next. Absent means "not paged yet"; null means the room is exhausted.
    /// </summary>
    private readonly Dictionary<string, string?> _cursors = [];

    /// <summary>
    /// Rows prepended since the renderer last looked. The virtual list must be told
    /// (<c>VirtualListInserted</c>) before the rebind, or inserting at the top scrolls the
    /// viewport by exactly the height of what was inserted — the jump this avoids.
    /// </summary>
    private int _prepended;

    public ChatModel Model { get; } = new();

    /// <summary>How many messages a room keeps before the oldest are dropped. Older history is
    /// still on the server and is re-fetchable with <c>HISTORY_REQ</c>.</summary>
    public int RoomScrollback { get; init; } = 5_000;

    /// <summary>
    /// Shows the attach control. Called by a head that wired a file dialog; without it the button
    /// stays hidden rather than sitting there unable to open anything.
    /// </summary>
    public void EnableAttach() => Model.AttachButtonClass = "attach-open";

    /// <summary>Sets an unread count and its visibility together, so the two cannot disagree.</summary>
    private static void SetBadge(RoomRow tab, string badge)
    {
        tab.Badge = badge;
        tab.BadgeClass = badge.Length == 0 ? "badge hidden" : "badge";
    }

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

    /// <summary>
    /// Number of rows prepended to the visible room since the last call, and resets. The app
    /// hands this to <c>CupriDocument.VirtualListInserted</c> before refreshing.
    /// </summary>
    public int TakePrependedCount()
    {
        var n = _prepended;
        _prepended = 0;
        return n;
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
            Model.Rooms.Add(new RoomRow { Name = room, Label = room });
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
                SetBadge(tab, "");
            }
        }

        // Rebind the visible list to this room's backlog. Replacing the list contents rather
        // than the list itself keeps the binding path (`Messages`) stable.
        Model.Messages.Clear();
        Model.Messages.AddRange(_rooms[room]);

        // The switch is a wholesale rebind, not an insertion — any pending prepend count belongs
        // to the room we just left and would misalign this room's scroll anchor.
        _prepended = 0;
        RefreshLoadOlderVisibility();
        ShowAgentsFor(room);
        ShowTasksFor(room);
    }

    public void SetTopic(string room, string topic)
    {
        if (room == Model.ActiveRoom)
        {
            Model.Topic = topic;
        }
    }

    public MessageRow Append(
        string room, string sender, string text, long timestamp,
        string rowClass = "line", string id = "", string fileId = "")
    {
        var row = new MessageRow
        {
            Id = id,
            Sender = sender,
            Text = text,
            Time = FormatTime(timestamp),
            // An egress announcement is the one message in a room that must never be skimmed
            // past, so it is styled apart from ordinary agent chatter.
            RowClass = text.StartsWith("[egress]", StringComparison.Ordinal)
                ? "line egress"
                : sender == Model.Nick && rowClass == "line" ? "line own" : rowClass,
            FileId = fileId,
            // Metadata arrives on a separate round-trip, so show the row immediately with a
            // placeholder rather than withholding it until the name and size are known.
            AttachClass = fileId.Length > 0 ? "attach" : "attach hidden",
            AttachText = fileId.Length > 0 ? "attachment" : "",
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
                SetBadge(tab, tab.Badge.Length == 0
                    ? "1"
                    : (int.TryParse(tab.Badge, out var n) ? n + 1 : 1).ToString());
            }
        }

        return row;
    }

    public void System(string room, string text) => Append(room, "*", text, 0, "line system");

    /// <summary>
    /// Show a downloaded image inline. Applies to every room, since the same file can be granted
    /// to more than one.
    /// </summary>
    public void SetInlineImage(string fileId, string localPath)
    {
        if (fileId.Length == 0 || localPath.Length == 0)
        {
            return;
        }

        var src = new Uri(localPath).AbsoluteUri;
        foreach (var row in _rooms.Values.SelectMany(rows => rows).Where(r => r.FileId == fileId))
        {
            row.ImageSrc = src;
            row.ImageClass = "inline-image";
        }
    }

    /// <summary>
    /// Fill in an attachment's name and size once the server has described it. Applies to every
    /// room, because the same file can be granted to more than one.
    /// </summary>
    public void SetAttachmentInfo(string fileId, string name, long size)
    {
        if (fileId.Length == 0)
        {
            return;
        }

        var label = $"{name} ({FormatSize(size)})";
        foreach (var row in _rooms.Values.SelectMany(rows => rows).Where(r => r.FileId == fileId))
        {
            row.AttachText = label;
        }
    }

    /// <summary>Byte count in the largest unit that keeps it readable.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    // ── Older history ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records where the next page of history starts. <paramref name="cursor"/> null means the
    /// server has nothing older, which is what hides the load-earlier control.
    /// </summary>
    public void SetHistoryCursor(string room, string? cursor)
    {
        _cursors[room] = cursor;
        if (room == Model.ActiveRoom)
        {
            RefreshLoadOlderVisibility();
        }
    }

    /// <summary>The cursor to pass as <c>beforeMessageId</c>, or null when there is no more.</summary>
    public string? HistoryCursor(string room) => _cursors.GetValueOrDefault(room);

    public bool CanLoadOlder(string room) => _cursors.GetValueOrDefault(room) is not null;

    /// <summary>
    /// Insert a page of older messages above what is already shown, oldest first. Returns how many
    /// rows were actually added to the <em>visible</em> list — messages already present by id are
    /// skipped, so a page that overlaps the live feed cannot duplicate anything.
    /// </summary>
    public int Prepend(string room, IReadOnlyList<(string Id, string Sender, string Text, long Timestamp)> older)
    {
        if (!_rooms.TryGetValue(room, out var backlog))
        {
            return 0;
        }

        var known = backlog.Where(r => r.Id.Length > 0).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var rows = new List<MessageRow>(older.Count);
        foreach (var (id, sender, text, timestamp) in older)
        {
            if (id.Length > 0 && !known.Add(id))
            {
                continue;
            }

            rows.Add(new MessageRow
            {
                Id = id,
                Sender = sender,
                Text = text,
                Time = FormatTime(timestamp),
                RowClass = sender == Model.Nick ? "line own" : "line",
            });
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        backlog.InsertRange(0, rows);

        if (room != Model.ActiveRoom)
        {
            return 0;
        }

        Model.Messages.InsertRange(0, rows);

        // Deliberately not trimming to RoomScrollback here: the user has just asked to see
        // further back, so dropping the oldest rows would undo the very thing they requested.
        _prepended += rows.Count;
        return rows.Count;
    }

    private void RefreshLoadOlderVisibility()
    {
        var can = CanLoadOlder(Model.ActiveRoom);
        Model.LoadOlderClass = can ? "loadmore" : "loadmore hidden";
    }

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

    // ── Room list ────────────────────────────────────────────────────────────────────────────

    /// <summary>Parent of each room the server told us about, for showing the list as a tree.</summary>
    private readonly Dictionary<string, string?> _roomParents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Take the server's room listing: label joined rooms with their parentage, and offer the
    /// rest for joining. A room you were put into by an agent or an admin rule appears here the
    /// same as any other, which is the point — nothing an agent opens is hidden from you.
    /// </summary>
    public void SetRoomListing(IEnumerable<(string Name, string? Parent, int Members)> rooms)
    {
        var joined = Model.Rooms.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Model.Browse.Clear();

        foreach (var (name, parent, members) in rooms.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            _roomParents[name] = parent;

            if (joined.Contains(name))
            {
                continue;
            }

            Model.Browse.Add(new BrowseRow
            {
                Name = name,
                Label = Label(name, parent),
                Members = members == 1 ? "1 member" : $"{members} members",
            });
        }

        Model.BrowseClass = Model.Browse.Count > 0 ? "browse" : "browse hidden";
        RelabelJoinedRooms();
    }

    private void RelabelJoinedRooms()
    {
        foreach (var row in Model.Rooms)
        {
            row.Label = Label(row.Name, _roomParents.GetValueOrDefault(row.Name));
        }
    }

    /// <summary>A sub-room is shown indented under its parent rather than as a peer.</summary>
    private static string Label(string name, string? parent) =>
        parent is { Length: > 0 } ? "  └ " + name : name;

    // ── Agent roster (PLAN §8a) ──────────────────────────────────────────────────────────────

    /// <summary>Roster per room, so switching rooms shows that room's agents.</summary>
    private readonly Dictionary<string, List<AgentRow>> _agents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replace a room's agent roster. <paramref name="delegator"/> is highlighted, and frontier
    /// agents are marked — a human should be able to see at a glance whether anything in this room
    /// is a third party, without having to remember which nick is which.
    /// </summary>
    public void SetAgents(string room, IEnumerable<(string Nick, bool IsLocal, string Skills, bool IsDelegator)> agents)
    {
        var rows = agents.Select(a => new AgentRow
        {
            Nick = a.Nick,
            Locality = a.IsLocal ? "local" : "frontier",
            Skills = a.Skills,
            Role = a.IsDelegator ? "delegator" : "",
            RowClass = a.IsDelegator ? "agent delegator" : a.IsLocal ? "agent" : "agent frontier",
        }).ToList();

        _agents[room] = rows;
        if (room == Model.ActiveRoom)
        {
            ShowAgentsFor(room);
        }
    }

    public void SetDelegator(string room, string? nick)
    {
        if (room != Model.ActiveRoom)
        {
            return;
        }

        Model.Delegator = nick is { Length: > 0 } ? nick : "no delegator";
        RefreshDispatch();
    }

    public void SetDispatchMode(string room, string mode)
    {
        if (room == Model.ActiveRoom)
        {
            Model.DispatchMode = mode;
            RefreshDispatch();
        }
    }

    /// <summary>
    /// Joins the mode and the delegator for the header, with the separator only where there are
    /// two things to separate.
    /// </summary>
    private void RefreshDispatch()
    {
        var mode = Model.DispatchMode;
        var who = Model.Delegator;
        Model.Dispatch = (mode.Length, who.Length) switch
        {
            (0, 0) => "",
            (0, _) => who,
            (_, 0) => mode,
            _ => $"{mode} · {who}",
        };
    }

    private void ShowAgentsFor(string room)
    {
        Model.Agents.Clear();
        if (_agents.TryGetValue(room, out var rows))
        {
            Model.Agents.AddRange(rows);
        }

        var delegatorRow = Model.Agents.FirstOrDefault(a => a.Role.Length > 0);
        Model.Delegator = delegatorRow?.Nick ?? "no delegator";
        RefreshDispatch();
    }

    // ── Task board (PLAN §8b) ────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, Dictionary<string, TaskRow>> _tasks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Add or update one task. Terminal tasks are dropped from the board rather than accumulating:
    /// the panel answers "what is happening now", and the timeline already records what happened.
    /// </summary>
    public void SetTask(string room, string taskId, string title, string state, string? assignee)
    {
        var board = _tasks.TryGetValue(room, out var existing) ? existing : _tasks[room] = new(StringComparer.Ordinal);
        var terminal = state is "Done" or "Failed";

        if (terminal)
        {
            board.Remove(taskId);
        }
        else
        {
            var row = board.TryGetValue(taskId, out var found) ? found : board[taskId] = new TaskRow { TaskId = taskId };
            row.Title = title;
            row.Status = assignee is { Length: > 0 }
                ? $"{state.ToLowerInvariant()} · {assignee}"
                : state.ToLowerInvariant();
            row.RowClass = assignee is { Length: > 0 } ? "task held" : "task";
        }

        if (room == Model.ActiveRoom)
        {
            ShowTasksFor(room);
        }
    }

    /// <summary>Replace the whole board, for the initial load.</summary>
    public void SetTasks(string room, IEnumerable<(string TaskId, string Title, string State, string? Assignee)> tasks)
    {
        _tasks[room] = new Dictionary<string, TaskRow>(StringComparer.Ordinal);
        foreach (var (id, title, state, assignee) in tasks)
        {
            SetTask(room, id, title, state, assignee);
        }

        if (room == Model.ActiveRoom)
        {
            ShowTasksFor(room);
        }
    }

    private void ShowTasksFor(string room)
    {
        Model.Tasks.Clear();
        if (_tasks.TryGetValue(room, out var board))
        {
            Model.Tasks.AddRange(board.Values);
        }

        Model.TasksClass = Model.Tasks.Count > 0 ? "tasks" : "tasks hidden";
    }

    /// <summary>Rows currently held for a room — the backlog, not just what is on screen.</summary>
    public IReadOnlyList<MessageRow> Backlog(string room) =>
        _rooms.TryGetValue(room, out var rows) ? rows : [];

    private static string FormatTime(long unixMs) =>
        (unixMs == 0 ? DateTimeOffset.Now : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime())
        .ToString("HH:mm");
}
