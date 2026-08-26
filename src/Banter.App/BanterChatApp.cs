using CupriFace;
using CupriFace.Interaction;

namespace Banter.App;

/// <summary>
/// The Banter client UI: one <see cref="CupriApp"/> shared by every host head (desktop today,
/// Android and web later). It owns markup, styling and input wiring only — all state lives in
/// <see cref="ChatViewModel"/> and all network work behind <see cref="SendAsync"/>, so this class
/// is host-agnostic and can be driven headlessly in tests.
/// </summary>
public sealed class BanterChatApp(ChatViewModel viewModel) : CupriApp
{
    private CupriDocument? _doc;

    public ChatViewModel ViewModel { get; } = viewModel;

    /// <summary>
    /// Called when the user sends the composer's contents to the active room. Left as a hook so
    /// the app never references a transport: the desktop head points it at
    /// <c>BanterClient.SendMessageAsync</c>, tests point it at a list.
    /// </summary>
    public Func<string, string, Task> SendAsync { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>Called when the user selects a room tab. Hook so the head can fetch history.</summary>
    public Action<string> RoomSelected { get; init; } = _ => { };

    /// <summary>Called when the user asks for older history in a room.</summary>
    public Func<string, Task> LoadOlderAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>
    /// Called for composer input beginning with <c>/</c>. Slash commands are the file-transfer
    /// entry point because CupriFace exposes no native file picker — <c>/upload &lt;path&gt;</c>
    /// works identically on every head, and a picker can be added per-platform later without
    /// changing this seam.
    /// </summary>
    public Func<string, string, Task> CommandAsync { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>Called when the user clicks an attachment.</summary>
    public Func<string, Task> DownloadAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Called when the user picks a room from the browse list.</summary>
    public Func<string, Task> JoinRoomAsync { get; init; } = _ => Task.CompletedTask;

    public override string Title => "Banter";
    public override object Model => ViewModel.Model;
    public override int Width => 1100;
    public override int Height => 760;

    /// <summary>
    /// Drives the pump in <see cref="Present"/>. Messages arrive on socket threads with no input
    /// event to piggyback on, so the host must tick even when the user is idle. 20 Hz is well
    /// inside the measured per-delta cost (~0.8 ms) and cheap when nothing has changed.
    /// </summary>
    public override double RefreshIntervalSeconds => 0.05;

    public override string Html => """
        <div class="app">
          <div class="sidebar">
            <div class="brand">Banter</div>
            <div class="{{StatusClass}}">{{Status}}</div>
            <div class="rooms">
              <div class="{{TabClass}}" data-repeat="Rooms" data-room="{{Name}}">
                <span class="tab-name">{{Label}}</span><span class="badge">{{Badge}}</span>
              </div>
              <div class="{{BrowseClass}}">
                <div class="browse-title">Other rooms</div>
                <div class="browse-row" data-repeat="Browse" data-join="{{Name}}">
                  <span class="browse-name">{{Label}}</span><span class="browse-members">{{Members}}</span>
                </div>
              </div>
            </div>
            <div class="nick">{{Nick}}</div>
          </div>
          <div class="main">
            <div class="header">
              <span class="room-name">{{ActiveRoom}}</span>
              <span class="topic">{{Topic}}</span>
              <span class="dispatch">{{DispatchMode}} &#183; {{Delegator}}</span>
            </div>
            <div class="{{LoadOlderClass}}" data-load-older="1">{{LoadOlderText}}</div>
            <cupri-virtual class="timeline" height="620" item-height="34" anchor="bottom">
              <div class="{{RowClass}}" data-repeat="Messages">
                <span class="time">{{Time}}</span><span class="sender">{{Sender}}</span>
                <span class="text">{{Text}}<span class="{{AttachClass}}" data-file="{{FileId}}">{{AttachText}}</span></span>
              </div>
            </cupri-virtual>
            <div class="composer-row">
              <cupri-textarea class="composer" value="{{Composer}}" placeholder="Message"></cupri-textarea>
              <cupri-button class="send">Send</cupri-button>
            </div>
          </div>
          <div class="roster">
            <div class="{{TasksClass}}">
              <div class="roster-title">Work</div>
              <div class="{{RowClass}}" data-repeat="Tasks">
                <div class="task-title">{{Title}}</div>
                <div class="task-status">{{Status}}</div>
              </div>
            </div>
            <div class="roster-title">Agents</div>
            <div class="{{RowClass}}" data-repeat="Agents">
              <div class="agent-line"><span class="agent-nick">{{Nick}}</span><span class="agent-role">{{Role}}</span></div>
              <div class="agent-meta">{{Locality}}</div>
              <div class="agent-meta">{{Skills}}</div>
            </div>
          </div>
        </div>
        """;

    public override string Css => """
        body { background: #14161a; color: #e6e8eb; font-size: 14px; }
        .app { display: flex; flex-direction: row; height: 760px; }

        .sidebar { display: flex; flex-direction: column; width: 220px; background: #1b1e24; padding: 12px; }
        .brand { font-weight: bold; font-size: 16px; padding-bottom: 8px; }
        .status { font-size: 12px; padding: 4px 6px; border-radius: 4px; margin-bottom: 10px; }
        .status.on { color: #7fd88f; background: #16301c; }
        .status.off { color: #e88c8c; background: #341a1a; }
        .rooms { flex: 1; }
        .tab { display: flex; flex-direction: row; padding: 6px 8px; border-radius: 4px; }
        .tab.active { background: #2b313b; }
        .tab-name { flex: 1; }
        .badge { color: #14161a; background: #7fa7ff; border-radius: 8px; padding: 0 6px; font-size: 11px; }
        .nick { font-size: 12px; color: #8b93a1; padding-top: 8px; }

        .browse { padding-top: 14px; }
        .browse.hidden { display: none; }
        .browse-title { font-size: 11px; color: #5d6572; padding-bottom: 4px; }
        .browse-row { display: flex; flex-direction: row; padding: 4px 8px; border-radius: 4px; }
        .browse-name { flex: 1; color: #8b93a1; font-size: 12px; }
        .browse-members { color: #5d6572; font-size: 11px; }

        .main { display: flex; flex-direction: column; flex: 1; }
        .header { display: flex; flex-direction: row; padding: 10px 14px; background: #1b1e24; }
        .room-name { font-weight: bold; padding-right: 12px; }
        .topic { color: #8b93a1; }

        .loadmore { padding: 6px 14px; color: #7fa7ff; font-size: 12px; background: #191c22; }
        .loadmore.hidden { display: none; }

        .timeline { flex: 1; padding: 6px 0; }
        /* No fixed height: rows wrap and the virtual list measures them (CupriFace 0.4.0). */
        .line { display: flex; flex-direction: row; padding: 3px 14px; }
        .time { color: #5d6572; font-size: 12px; width: 44px; }
        .sender { color: #7fa7ff; width: 110px; }
        /* pre-wrap is load-bearing: messages carry hard newlines (agent replies are mostly
           paragraphs and code), and the CSS default would collapse them into one run-on line.
           Long lines still wrap. Requires CupriFace 0.5.0 or later. */
        .text { flex: 1; white-space: pre-wrap; }
        .line.own .sender { color: #7fd88f; }
        .line.system { color: #8b93a1; font-style: italic; }
        .line.system .sender { color: #8b93a1; }
        .line.streaming .text { color: #cdd3dc; }

        .attach { color: #7fa7ff; background: #1f2530; border-radius: 4px; padding: 1px 6px; margin-left: 6px; }
        .attach.hidden { display: none; }

        .dispatch { flex: 1; text-align: right; color: #8b93a1; font-size: 12px; }

        .roster { width: 190px; background: #1b1e24; padding: 12px; overflow: scroll; }

        .tasks { padding-bottom: 12px; }
        .tasks.hidden { display: none; }
        .task { padding: 6px 8px; border-radius: 4px; margin-bottom: 6px; background: #232833; }
        /* Held work reads differently from work still waiting for someone. */
        .task.held { background: #1f2b33; }
        .task-title { font-size: 12px; }
        .task-status { color: #8b93a1; font-size: 11px; }
        .roster-title { font-weight: bold; font-size: 12px; color: #8b93a1; padding-bottom: 8px; }
        .agent { padding: 6px 8px; border-radius: 4px; margin-bottom: 6px; background: #232833; }
        /* Frontier agents are marked, not merely listed: whether a third party is in the room is
           the thing a human most needs to be able to see at a glance. */
        .agent.frontier { background: #33261a; }
        .agent.delegator { background: #16301c; }
        .agent-line { display: flex; flex-direction: row; }
        .agent-nick { flex: 1; font-size: 13px; }
        .agent-role { color: #7fd88f; font-size: 11px; }
        .agent.frontier .agent-meta { color: #e0a56a; }
        .agent-meta { color: #8b93a1; font-size: 11px; }

        /* An egress notice must not read like ordinary chatter. */
        .line.egress { background: #33261a; }
        .line.egress .text { color: #e0a56a; }
        .line.egress .sender { color: #e0a56a; }

        .composer-row { display: flex; flex-direction: row; padding: 10px 14px; background: #1b1e24; }
        .composer { flex: 1; min-height: 44px; max-height: 120px; }
        .send { width: 90px; margin-left: 8px; }
        """;

    public override void Configure(CupriDocument doc)
    {
        _doc = doc;

        doc.OnClick(".send", _ => Send());

        // Ctrl+Enter sends; plain Enter stays a newline in the textarea, which is what a
        // multi-line composer needs and what the CupriFace guidance recommends.
        doc.OnShortcut(KeyMods.Ctrl, "Enter", Send);

        // Room tabs carry their own name, so one handler serves every repeated row.
        doc.OnAction("data-room", e =>
        {
            var room = e.Value;
            if (string.IsNullOrEmpty(room))
            {
                return false;
            }

            ViewModel.Post(() => ViewModel.SwitchTo(room));
            RoomSelected(room);
            return true;
        });

        doc.OnAction("data-load-older", _ =>
        {
            LoadOlder();
            return true;
        });

        doc.OnAction("data-join", e =>
        {
            if (string.IsNullOrEmpty(e.Value))
            {
                return false;
            }

            _ = JoinRoomAsync(e.Value);
            return true;
        });

        doc.OnAction("data-file", e =>
        {
            // Every row carries the attribute; only rows with a file have a value in it.
            if (string.IsNullOrEmpty(e.Value))
            {
                return false;
            }

            _ = DownloadAsync(e.Value);
            return true;
        });
    }

    /// <summary>
    /// Fetch the next page of older history for the active room. An explicit control rather than
    /// a scroll-position trigger: it works identically on desktop and touch, and it keeps the
    /// request deliberate instead of firing every time a fling overshoots the top.
    /// </summary>
    public void LoadOlder()
    {
        var room = ViewModel.Model.ActiveRoom;
        if (room.Length == 0 || !ViewModel.CanLoadOlder(room))
        {
            return;
        }

        _ = LoadOlderAsync(room);
    }

    /// <summary>
    /// The render-thread pump. Draining here is what makes the threading contract in
    /// <see cref="ChatViewModel"/> hold: mutations queued by socket threads are applied on this
    /// thread, immediately before the frame that will show them, and <c>Refresh()</c> runs only
    /// when something actually changed.
    /// </summary>
    public override PresentInfo Present(float width, float height)
    {
        if (ViewModel.ApplyPending())
        {
            // Order matters: the virtual list must learn about rows inserted at the top *before*
            // the rebind, so it can move its scroll anchor by the same amount. Refresh first and
            // the viewport jumps down by the height of the page just loaded.
            var prepended = ViewModel.TakePrependedCount();
            if (prepended > 0)
            {
                _doc?.VirtualListInserted("Messages", 0, prepended);
            }

            _doc?.Refresh();
        }

        return base.Present(width, height);
    }

    /// <summary>Send the composer's contents to the active room and clear it.</summary>
    public void Send()
    {
        var text = ViewModel.Model.Composer.Trim();
        var room = ViewModel.Model.ActiveRoom;
        if (text.Length == 0 || room.Length == 0)
        {
            return;
        }

        ViewModel.Model.Composer = "";
        _doc?.Refresh();

        if (text[0] == '/')
        {
            _ = CommandAsync(room, text);
            return;
        }

        // Fire-and-forget by design: the authoritative message arrives back as a MSG echo, so
        // there is nothing to await here. Failures surface through the client's error events.
        _ = SendAsync(room, text);
    }
}
