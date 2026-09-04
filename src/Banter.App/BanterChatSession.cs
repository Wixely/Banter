using Banter.Client.Core;

namespace Banter.App;

/// <summary>
/// Wires a live <see cref="BanterClient"/> to a <see cref="ChatViewModel"/>. Every handler does
/// nothing but <c>Post</c> a closure — no handler touches the model directly, because they all run
/// on the client's receive loop rather than the render thread.
///
/// <para>Kept separate from <see cref="BanterChatApp"/> so the app has no transport dependency and
/// the tests can exercise the timeline without a server.</para>
/// </summary>
public sealed partial class BanterChatSession : IDisposable
{
    private readonly BanterClient _client;
    private readonly ChatViewModel _vm;

    /// <summary>Rooms with a history page in flight. Guarded by the lock below because the
    /// control can be clicked from the render thread while a fetch completes on another.</summary>
    private readonly HashSet<string> _loadingRooms = [];

    private bool _disposed;

    /// <summary>Try to claim the "loading older history" slot for a room.</summary>
    private bool BeginLoad(string room)
    {
        lock (_loadingRooms)
        {
            return _loadingRooms.Add(room);
        }
    }

    private void EndLoad(string room)
    {
        lock (_loadingRooms)
        {
            _loadingRooms.Remove(room);
        }
    }

    public BanterChatSession(BanterClient client, ChatViewModel viewModel)
    {
        _client = client;
        _vm = viewModel;

        _client.MessageReceived += OnMessage;
        _client.DelegatorChanged += OnDelegatorChanged;
        _client.TaskChanged += OnTaskChanged;
        _client.RoomModeChanged += OnRoomModeChanged;
        _client.MemberJoined += OnJoined;
        _client.MemberParted += OnParted;
        _client.TopicChanged += OnTopic;
        _client.MessageStreamStarted += OnStreamStart;
        _client.MessageStreamDelta += OnStreamDelta;
        _client.MessageStreamEnded += OnStreamEnd;
        _client.PrivateMessageReceived += OnPrivate;
        _client.MessageEdited += OnEdited;
        _client.MessageDeleted += OnDeleted;
        _client.ServerError += OnServerError;
        _client.Disconnected += OnDisconnected;
        _client.Evicted += OnEvicted;
        _client.Reconnecting += OnReconnecting;
        _client.Reconnected += OnReconnected;
    }

    /// <summary>Joins a room and back-fills it from server history so the timeline isn't empty.</summary>
    public async Task JoinAsync(string room, int history = 100, CancellationToken cancellationToken = default)
    {
        await _client.JoinAsync(room, cancellationToken).ConfigureAwait(false);
        _vm.Post(() =>
        {
            _vm.AddRoom(room);
            _vm.SetNick(_client.Nick);

            // The rail's agents button appears only for an operator. The server refuses everybody
            // else anyway, and a button that always ends in NOT_ADMIN is worse than no button.
            _vm.SetIsAdmin(_client.IsAdmin);
        });

        var page = await _client.GetHistoryAsync(room, limit: history, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _vm.Post(() =>
        {
            foreach (var m in page.Messages)
            {
                _vm.Append(room, m.Sender, m.Text, m.Timestamp, id: m.MessageId ?? "");
            }

            _vm.SetHistoryCursor(room, page.NextCursor);
        });

        await RefreshRosterAsync(room, cancellationToken).ConfigureAwait(false);
        await RefreshTasksAsync(room, cancellationToken).ConfigureAwait(false);
        await RefreshRoomsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-read the server's room list. Also called when someone joins, because that is how a
    /// room an agent just opened - or one an admin was put into - shows up without a restart.
    /// </summary>
    public async Task RefreshRoomsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var listing = await _client.ListRoomsAsync(cancellationToken).ConfigureAwait(false);
            _vm.Post(() => _vm.SetRoomListing(
                listing.Rooms.Select(r => (r.Name, r.ParentRoom, r.MemberCount))));
        }
        catch (Exception)
        {
            // Best effort; the list is a view.
        }
    }

    /// <summary>Load the room's live task board. Terminal tasks are left out: the panel answers
    /// "what is happening now", and the timeline already records what happened.</summary>
    public async Task RefreshTasksAsync(string room, CancellationToken cancellationToken = default)
    {
        try
        {
            var board = await _client.ListTasksAsync(room, includeFinished: false, cancellationToken)
                .ConfigureAwait(false);
            _vm.Post(() => _vm.SetTasks(
                room,
                board.Tasks.Select(t => (t.TaskId, t.Title, t.State.ToString(), t.Assignee))));
        }
        catch (Exception)
        {
            // Best effort; the board is a view, not the record.
        }
    }

    private void OnTaskChanged(Protocol.TaskInfoPayload t) =>
        _vm.Post(() => _vm.SetTask(t.Room, t.TaskId, t.Title, t.State.ToString(), t.Assignee));

    /// <summary>
    /// Re-read who is in the room — agents and humans both. Called on join and whenever
    /// membership or the delegator changes, because a roster that only reflects the moment you
    /// joined would quietly stop telling the truth — including about whether a third-party agent
    /// is present.
    ///
    /// <para>Two reads on purpose: the agent listing carries the routing attributes only agents
    /// have, and the member listing is where the humans are. Agents appear in both, so the member
    /// half keeps only the people.</para>
    /// </summary>
    public async Task RefreshRosterAsync(string room, CancellationToken cancellationToken = default)
    {
        try
        {
            var roster = await _client.GetAgentsAsync(room, cancellationToken).ConfigureAwait(false);
            _vm.Post(() => _vm.SetAgents(
                room,
                roster.Agents.Select(a => (
                    a.Nick,
                    IsLocal: a.Locality == Protocol.AgentLocality.Local,
                    Skills: string.Join(", ", a.Skills),
                    a.IsDelegator))));

            var members = await _client.GetMembersAsync(room, cancellationToken).ConfigureAwait(false);
            _vm.Post(() => _vm.SetRoomUsers(
                room,
                members.Members.Where(m => !m.IsAgent).Select(m => (m.Nick, m.Modes))));
        }
        catch (Exception)
        {
            // Best effort: a stale roster is a display issue, not a correctness one.
        }
    }

    private void OnDelegatorChanged(Protocol.RoomDelegatorPayload p)
    {
        _vm.Post(() => _vm.SetDelegator(p.Room, p.Nick));
        _ = RefreshRosterAsync(p.Room);
    }

    private void OnRoomModeChanged(Protocol.RoomModePayload p) =>
        _vm.Post(() => _vm.SetDispatchMode(p.Room, p.Mode.ToString().ToLowerInvariant()));

    /// <summary>
    /// Fetch the next page of older history and splice it above what is shown. Re-entrancy is
    /// guarded per room: the control stays clickable, but a second click while a page is still in
    /// flight would page past the cursor and leave a hole in the timeline.
    /// </summary>
    public async Task LoadOlderAsync(string room, int limit = 100, CancellationToken cancellationToken = default)
    {
        var cursor = _vm.HistoryCursor(room);
        if (cursor is null || !BeginLoad(room))
        {
            return;
        }

        try
        {
            var page = await _client
                .GetHistoryAsync(room, beforeMessageId: cursor, limit: limit, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var older = page.Messages
                .Select(m => (Id: m.MessageId ?? "", m.Sender, m.Text, m.Timestamp))
                .ToList();

            _vm.Post(() =>
            {
                _vm.Prepend(room, older);
                _vm.SetHistoryCursor(room, page.NextCursor);
            });
        }
        catch (Exception ex)
        {
            _vm.Post(() => _vm.System(room, $"could not load earlier messages: {ex.Message}"));
        }
        finally
        {
            EndLoad(room);
        }
    }

    public Task PartAsync(string room, CancellationToken cancellationToken = default)
    {
        _vm.Post(() => _vm.RemoveRoom(room));
        return _client.PartAsync(room, cancellationToken: cancellationToken);
    }

    public Task SendAsync(string room, string text) =>
        _client.SendMessageAsync(room, text).AsTask();

    /// <summary>Where downloads land. Defaults to the user's Downloads folder.</summary>
    public string DownloadDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// Where image previews are cached. Separate from downloads: these are fetched automatically
    /// rather than asked for, so they do not belong in the user's Downloads folder.
    /// </summary>
    public string ImageCacheDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "banter-images");

    /// <summary>
    /// Largest image fetched automatically for a preview. Everything above it stays a chip the
    /// user can click - showing a picture is not worth spending someone's bandwidth without
    /// being asked.
    /// </summary>
    public long MaxInlineImageBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Images already fetched or being fetched, so a re-render cannot re-download.</summary>
    private readonly HashSet<string> _fetchedImages = new(StringComparer.Ordinal);

    /// <summary>
    /// Fetch an image attachment for inline display, if it is one and small enough. Best effort
    /// throughout: a preview that fails to load is a missing picture, not a broken timeline.
    /// </summary>
    private async Task TryFetchInlineImageAsync(Protocol.FileInfoPayload info)
    {
        if (!MimeTypes.IsImage(info.MimeType) || info.Size > MaxInlineImageBytes)
        {
            return;
        }

        lock (_fetchedImages)
        {
            if (!_fetchedImages.Add(info.FileId))
            {
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(ImageCacheDirectory);

            // Keyed by file id, which is content-addressed server-side, so a cached preview can
            // never be a different picture than the one the message refers to.
            var cached = Path.Combine(ImageCacheDirectory, info.FileId + Path.GetExtension(info.Name));
            if (!File.Exists(cached))
            {
                var bytes = await _client.DownloadFileAsync(info.FileId).ConfigureAwait(false);
                await File.WriteAllBytesAsync(cached, bytes).ConfigureAwait(false);
            }

            _vm.Post(() => _vm.SetInlineImage(info.FileId, cached));
        }
        catch (Exception)
        {
            // Allow a retry on the next mention of this file rather than never trying again.
            lock (_fetchedImages)
            {
                _fetchedImages.Remove(info.FileId);
            }
        }
    }

    /// <summary>
    /// Handle composer input starting with <c>/</c>. Unknown commands report themselves rather
    /// than being sent to the room as text, so a typo cannot leak a half-typed command to everyone.
    /// </summary>
    public async Task CommandAsync(string room, string line)
    {
        var space = line.IndexOf(' ');
        var verb = (space < 0 ? line : line[..space])[1..].ToLowerInvariant();
        var rest = space < 0 ? "" : line[(space + 1)..].Trim();

        switch (verb)
        {
            case "upload" when rest.Length > 0:
                await UploadAsync(room, rest).ConfigureAwait(false);
                break;
            case "upload":
                _vm.Post(() => _vm.System(room, "usage: /upload <path> [description]"));
                break;
            case "files":
                await ListFilesAsync(room).ConfigureAwait(false);
                break;
            case "edit" when rest.Length > 0:
                await EditLastAsync(room, rest).ConfigureAwait(false);
                break;
            case "edit":
                _vm.Post(() => _vm.System(room, "usage: /edit <the corrected message>"));
                break;
            case "delete":
                await DeleteLastAsync(room).ConfigureAwait(false);
                break;
            case "task" when rest.Length > 0:
                await Guard(room, async () =>
                {
                    var (title, body) = SplitTitleAndBody(rest);
                    await _client.PostTaskAsync(room, title, body).ConfigureAwait(false);
                }).ConfigureAwait(false);
                break;
            case "task":
                _vm.Post(() => _vm.System(room, "usage: /task <title> [-- details]"));
                break;
            case "rooms":
                await RefreshRoomsAsync().ConfigureAwait(false);
                break;
            case "join" when rest.Length > 0:
                await Guard(room, () => JoinAsync(rest.Trim())).ConfigureAwait(false);
                break;
            case "tasks":
                await RefreshTasksAsync(room).ConfigureAwait(false);
                await ShowTasksAsync(room).ConfigureAwait(false);
                break;
            case "topic" when rest.Length > 0:
                await Guard(room, () => _client.SetTopicAsync(room, rest).AsTask()).ConfigureAwait(false);
                break;
            case "part":
                await Guard(room, () => PartAsync(room)).ConfigureAwait(false);
                break;
            case "tools":
                // The panel is the real surface; the command is here so it is reachable without
                // hunting for a button, and so it works on a head that has no roster on screen.
                _vm.Post(() =>
                {
                    if (rest.Length > 0)
                    {
                        _vm.SelectToolAgent(rest);
                    }

                    _vm.ShowToolPanel(true);
                });
                await LoadToolsAsync(rest).ConfigureAwait(false);
                break;
            case "help":
                _vm.Post(() => _vm.System(room,
                    "/upload <path> [description] | /files | /task <title> [-- details] | /tasks | " +
                    "/edit <text> | /delete | " +
                    "/join #room | /rooms | /topic <text> | /tools [agent] | /part | /help"));
                break;
            default:
                _vm.Post(() => _vm.System(room, $"unknown command: /{verb} (try /help)"));
                break;
        }
    }

    /// <summary>Upload a local file into the room. The path may be quoted to allow spaces.</summary>
    public async Task UploadAsync(string room, string argument)
    {
        var (path, description) = SplitPathAndDescription(argument);

        if (!File.Exists(path))
        {
            _vm.Post(() => _vm.System(room, $"no such file: {path}"));
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var name = Path.GetFileName(path);
            _vm.Post(() => _vm.System(room, $"uploading {name} ({ChatViewModel.FormatSize(bytes.Length)})..."));

            var info = await _client
                .UploadFileAsync(room, name, bytes, MimeTypes.ForFile(name), description)
                .ConfigureAwait(false);

            _vm.Post(() => _vm.SetAttachmentInfo(info.FileId, info.Name, info.Size));

            // Show our own upload inline too, from the bytes we already have - no round trip.
            if (MimeTypes.IsImage(info.MimeType) && bytes.LongLength <= MaxInlineImageBytes)
            {
                Directory.CreateDirectory(ImageCacheDirectory);
                var cached = Path.Combine(ImageCacheDirectory, info.FileId + Path.GetExtension(info.Name));
                await File.WriteAllBytesAsync(cached, bytes).ConfigureAwait(false);
                lock (_fetchedImages)
                {
                    _fetchedImages.Add(info.FileId);
                }

                _vm.Post(() => _vm.SetInlineImage(info.FileId, cached));
            }
        }
        catch (Exception ex)
        {
            _vm.Post(() => _vm.System(room, $"upload failed: {ex.Message}"));
        }
    }

    /// <summary>Fetch a file and write it beside the user's other downloads.</summary>
    public async Task DownloadAsync(string fileId)
    {
        var room = _vm.Model.ActiveRoom;
        try
        {
            var info = await _client.GetFileInfoAsync(fileId).ConfigureAwait(false);
            var bytes = await _client.DownloadFileAsync(fileId).ConfigureAwait(false);

            Directory.CreateDirectory(DownloadDirectory);
            var target = UniquePath(Path.Combine(DownloadDirectory, SafeName(info.Name)));
            await File.WriteAllBytesAsync(target, bytes).ConfigureAwait(false);

            _vm.Post(() => _vm.System(room, $"saved {target}"));
        }
        catch (Exception ex)
        {
            _vm.Post(() => _vm.System(room, $"download failed: {ex.Message}"));
        }
    }

    /// <summary>Splits <c>title -- details</c>; without the separator it is all title.</summary>
    public static (string Title, string Body) SplitTitleAndBody(string text)
    {
        var marker = text.IndexOf("--", StringComparison.Ordinal);
        return marker < 0
            ? (text.Trim(), "")
            : (text[..marker].Trim(), text[(marker + 2)..].Trim());
    }

    private async Task ShowTasksAsync(string room)
    {
        try
        {
            var board = await _client.ListTasksAsync(room).ConfigureAwait(false);
            _vm.Post(() =>
            {
                if (board.Tasks.Count == 0)
                {
                    _vm.System(room, "no open work in this room");
                    return;
                }

                foreach (var t in board.Tasks)
                {
                    var who = t.Assignee is { Length: > 0 } a ? $" ({a})" : "";
                    _vm.System(room, $"[{t.State.ToString().ToLowerInvariant()}]{who} {t.Title}");
                }
            });
        }
        catch (Exception ex)
        {
            _vm.Post(() => _vm.System(room, $"could not list work: {ex.Message}"));
        }
    }

    private async Task ListFilesAsync(string room)
    {
        try
        {
            var list = await _client.ListFilesAsync(room).ConfigureAwait(false);
            _vm.Post(() =>
            {
                if (list.Files.Count == 0)
                {
                    _vm.System(room, "no files in this room");
                    return;
                }

                foreach (var f in list.Files)
                {
                    // Rendered as a real attachment row, so it is clickable like any other.
                    _vm.Append(room, "*", $"{f.Uploader} shared", f.CreatedAt, "line system", fileId: f.FileId);
                    _vm.SetAttachmentInfo(f.FileId, f.Name, f.Size);
                }

                foreach (var f in list.Files)
                {
                    _ = TryFetchInlineImageAsync(f);
                }
            });
        }
        catch (Exception ex)
        {
            _vm.Post(() => _vm.System(room, $"could not list files: {ex.Message}"));
        }
    }

    private async Task Guard(string room, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _vm.Post(() => _vm.System(room, ex.Message));
        }
    }

    /// <summary>
    /// Splits <c>"path with spaces" description</c> or <c>path description</c>. Public because it
    /// is a pure function with awkward edge cases (an unquoted path that itself contains spaces)
    /// worth testing directly rather than through a live upload.
    /// </summary>
    public static (string Path, string? Description) SplitPathAndDescription(string argument)
    {
        argument = argument.Trim();
        if (argument.StartsWith('"'))
        {
            var close = argument.IndexOf('"', 1);
            if (close > 0)
            {
                var desc = argument[(close + 1)..].Trim();
                return (argument[1..close], desc.Length > 0 ? desc : null);
            }
        }

        // Unquoted: if what precedes the first space is a real file, the rest is a description.
        var space = argument.IndexOf(' ');
        if (space > 0 && File.Exists(argument[..space]))
        {
            return (argument[..space], argument[(space + 1)..].Trim());
        }

        return (argument, null);
    }

    /// <summary>Strip any directory component a server-supplied name might carry.</summary>
    private static string SafeName(string name)
    {
        var bare = Path.GetFileName(name);
        return string.IsNullOrWhiteSpace(bare) ? "download" : bare;
    }

    /// <summary>Never silently overwrite something already in the downloads folder.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    // The id matters beyond display: it is what stops a page of older history re-adding a
    // message the live feed already delivered.
    private void OnMessage(Protocol.MsgPayload m)
    {
        _vm.Post(() => _vm.Append(m.Room, m.Sender, m.Text, m.Timestamp, id: m.MessageId ?? "", fileId: m.FileId ?? ""));
        SpeakIncoming(m.Room, m.Sender, m.Text);

        // The message only carries a file id, so name and size need a round-trip. Done off the
        // receive loop and best-effort: the row is already visible with a placeholder, and a
        // failure here should not disturb the timeline.
        if (m.FileId is { Length: > 0 } fileId)
        {
            _ = FetchAttachmentInfoAsync(fileId);
        }
    }

    /// <summary>Rewrites a message by id, for the right-click menu.</summary>
    public Task EditAsync(string room, string messageId, string text) =>
        Guard(room, () => _client.EditMessageAsync(room, messageId, text).AsTask());

    /// <summary>Takes back a message by id, for the right-click menu.</summary>
    public Task DeleteAsync(string room, string messageId) =>
        Guard(room, () => _client.DeleteMessageAsync(room, messageId).AsTask());

    /// <summary>
    /// Changes the last thing this account said here. Acting on "what I just said" rather than on
    /// a chosen message, because the timeline is painted to a canvas and has no per-message
    /// affordance yet — and asking somebody to find a message id would be worse than no feature.
    /// </summary>
    private async Task EditLastAsync(string room, string text)
    {
        var id = _vm.LastOwnMessageId(room);
        if (id.Length == 0)
        {
            _vm.Post(() => _vm.System(room, "nothing of yours to edit here."));
            return;
        }

        await Guard(room, () => _client.EditMessageAsync(room, id, text).AsTask()).ConfigureAwait(false);
    }

    /// <summary>Takes back the last thing this account said here.</summary>
    private async Task DeleteLastAsync(string room)
    {
        var id = _vm.LastOwnMessageId(room);
        if (id.Length == 0)
        {
            _vm.Post(() => _vm.System(room, "nothing of yours to delete here."));
            return;
        }

        await Guard(room, () => _client.DeleteMessageAsync(room, id).AsTask()).ConfigureAwait(false);
    }

    private void OnEdited(Protocol.EditPayload e) =>
        _vm.Post(() => _vm.MarkEdited(e.Room, e.MessageId, e.Text));

    private void OnDeleted(Protocol.DeletePayload d) =>
        _vm.Post(() => _vm.MarkDeleted(d.Room, d.MessageId));

    private async Task FetchAttachmentInfoAsync(string fileId)
    {
        try
        {
            var info = await _client.GetFileInfoAsync(fileId).ConfigureAwait(false);
            _vm.Post(() => _vm.SetAttachmentInfo(info.FileId, info.Name, info.Size));
            await TryFetchInlineImageAsync(info).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Leave the placeholder label; the attachment is still downloadable by id.
        }
    }

    private void OnJoined(Protocol.JoinPayload j)
    {
        _vm.Post(() => _vm.System(j.Room, $"{j.Nick} joined"));
        _ = RefreshRosterAsync(j.Room);

        // Being put into a room by the admin rule or a delegator arrives as a join for us, with
        // no JoinAsync of our own to hang the setup off.
        if (string.Equals(j.Nick, _client.Nick, StringComparison.OrdinalIgnoreCase))
        {
            _ = AdoptRoomAsync(j.Room);
        }
    }

    /// <summary>Set up a room the server put us in, rather than one we asked to join.</summary>
    private async Task AdoptRoomAsync(string room)
    {
        _vm.Post(() => _vm.AddRoom(room));
        await RefreshRoomsAsync().ConfigureAwait(false);
        await RefreshTasksAsync(room).ConfigureAwait(false);
    }

    private void OnParted(Protocol.PartPayload p)
    {
        _vm.Post(() => _vm.System(p.Room, p.Reason is { Length: > 0 } r ? $"{p.Nick} left ({r})" : $"{p.Nick} left"));
        _ = RefreshRosterAsync(p.Room);
    }

    private void OnTopic(Protocol.TopicPayload t) =>
        _vm.Post(() =>
        {
            _vm.SetTopic(t.Room, t.Topic);
            _vm.System(t.Room, $"Topic: {t.Topic}");
        });

    private void OnStreamStart(Protocol.MsgStreamStartPayload s)
    {
        _vm.Post(() => _vm.StreamStart(s.Room, s.Sender, s.StreamId));
        BeginSpokenStream(s.Room, s.StreamId, s.Sender);
    }

    private void OnStreamDelta(Protocol.MsgStreamDeltaPayload d)
    {
        _vm.Post(() => _vm.StreamDelta(d.StreamId, d.Delta));

        // Spoken as the sentences complete rather than at the end (§6): waiting for the stream to
        // finish puts the whole generation time in front of the first sound.
        SpeakDelta(d.StreamId, d.Delta);
    }

    private void OnStreamEnd(Protocol.MsgStreamEndPayload e)
    {
        _vm.Post(() => _vm.StreamEnd(e.StreamId, e.FinalText, e.Timestamp));
        EndSpokenStream(e.StreamId);
    }

    private void OnPrivate(Protocol.PrivMsgPayload p) =>
        _vm.Post(() => _vm.System(_vm.Model.ActiveRoom, $"[pm] {p.Sender}: {p.Text}"));

    // Errors that match no outstanding request land here — including a refused send, which is
    // how an agent learns it has been throttled. Showing them in-timeline keeps that visible.
    private void OnServerError(Protocol.ErrorPayload e) =>
        _vm.Post(() => _vm.System(_vm.Model.ActiveRoom, $"server: {e.Code} {e.Message}"));

    private void OnDisconnected() =>
        _vm.Post(() => _vm.SetStatus("Disconnected", connected: false));

    // The server said goodbye on purpose and said why - the credential this client signed in with
    // was removed, reset or re-roled. The reason is the status, because "Disconnected" would send
    // someone checking their network instead of their standing.
    private void OnEvicted(string reason) =>
        _vm.Post(() => _vm.SetStatus(reason, connected: false));

    private void OnReconnecting(int attempt) =>
        _vm.Post(() => _vm.SetStatus($"Reconnecting ({attempt})", connected: false));

    private void OnReconnected() =>
        _vm.Post(() => _vm.SetStatus("Connected", connected: true));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.MessageReceived -= OnMessage;
        _client.DelegatorChanged -= OnDelegatorChanged;
        _client.TaskChanged -= OnTaskChanged;
        _client.RoomModeChanged -= OnRoomModeChanged;
        _client.MemberJoined -= OnJoined;
        _client.MemberParted -= OnParted;
        _client.TopicChanged -= OnTopic;
        _client.MessageStreamStarted -= OnStreamStart;
        _client.MessageStreamDelta -= OnStreamDelta;
        _client.MessageStreamEnded -= OnStreamEnd;
        _client.PrivateMessageReceived -= OnPrivate;
        _client.MessageEdited -= OnEdited;
        _client.MessageDeleted -= OnDeleted;
        _client.ServerError -= OnServerError;
        _client.Disconnected -= OnDisconnected;
        _client.Evicted -= OnEvicted;
        _client.Reconnecting -= OnReconnecting;
        _client.Reconnected -= OnReconnected;
    }
}
