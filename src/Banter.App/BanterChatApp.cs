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

    /// <summary>
    /// Called when the tool-grants panel opens, and again whenever a different agent is selected
    /// in it — the argument is that agent's nick, or empty when the panel is merely opening. The
    /// head answers by fetching the catalogue and that agent's grants.
    /// </summary>
    public Func<string, Task> ToolsOpenAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Called when the operator saves: the selected agent and its complete new grant set.</summary>
    public Func<string, IReadOnlyList<string>, Task> ToolsSaveAsync { get; init; } =
        (_, _) => Task.CompletedTask;

    /// <summary>
    /// The system clipboard. Defaults to a no-op so the app runs headlessly; the desktop head
    /// supplies a real one.
    /// </summary>
    public IClipboard Clipboard { get; init; } = NullClipboard.Instance;

    /// <summary>
    /// Called when the user taps the microphone: true to open it, false to close it.
    ///
    /// <para>A toggle rather than a hold because CupriFace raises clicks, not pointer-down and
    /// pointer-up — there is no press to hold. Real hold-to-talk is the desktop global hotkey
    /// (PLAN §7), which has both edges; on screen, tap-to-talk is also the better fit for touch.</para>
    /// </summary>
    public Func<bool, Task> VoiceToggleAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Called when the user cycles the readback policy, with the policy now in force.</summary>
    public Func<Voice.ReadbackPolicy, Task> ReadbackChangedAsync { get; init; } = _ => Task.CompletedTask;

    public override string Title => "Banter";
    public override object Model => ViewModel.Model;
    public override int Width => 1100;
    public override int Height => 760;

    /// <summary>
    /// A dark title bar. Without it Windows gives the window its default light chrome, which on a
    /// dark app reads as a white band bolted to the top of it.
    /// </summary>
    public override bool DarkWindowChrome => true;

    /// <summary>
    /// The same colour the stylesheet paints the page. The host clears to this before the first
    /// frame and during a resize, so matching it removes the white flash on open and the white
    /// edge that trails a window being dragged wider.
    /// </summary>
    public override SkiaSharp.SKColor Background => new(0x14, 0x16, 0x1a);

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
                <span class="tab-name">{{Label}}</span><span class="{{BadgeClass}}">{{Badge}}</span>
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
              <span class="dispatch">{{Dispatch}}</span>
            </div>
            <div class="{{LoadOlderClass}}" data-load-older="1">{{LoadOlderText}}</div>
            <cupri-context-menu class="timeline-menu">
              <cupri-virtual class="timeline" height="620" item-height="34" anchor="bottom">
                <div class="{{RowClass}}" data-repeat="Messages">
                  <span class="time">{{Time}}</span><span class="sender">{{Sender}}</span>
                  <span class="text"><span class="body">{{Text}}</span><span class="{{AttachClass}}" data-file="{{FileId}}">{{AttachText}}</span><cupri-image class="{{ImageClass}}" src="{{ImageSrc}}" alt="{{AttachText}}"></cupri-image></span>
                </div>
              </cupri-virtual>
              <cupri-menu-item class="copy-selection">Copy</cupri-menu-item>
              <cupri-menu-item class="copy-image">Copy image</cupri-menu-item>
              <cupri-menu-item class="copy-room">Copy room name</cupri-menu-item>
            </cupri-context-menu>
            <div class="composer-row">
              <cupri-button class="{{MicClass}}">{{MicText}}</cupri-button>
              <cupri-textarea class="composer" value="{{Composer}}" placeholder="Message  ·  Ctrl+Enter to send"></cupri-textarea>
              <cupri-button class="send">Send</cupri-button>
            </div>
            <div class="{{VoiceRowClass}}">
              <span class="voice-status">{{VoiceStatus}}</span>
              <cupri-button class="{{ReadbackClass}}">{{ReadbackText}}</cupri-button>
            </div>
          </div>
          <div class="{{ToolsClass}}">
            <div class="toolpanel-inner">
              <div class="toolpanel-head">
                <span class="toolpanel-title">{{ToolsTitle}}</span>
                <span class="toolpanel-status">{{ToolsStatus}}</span>
                <cupri-button class="tools-save">Save</cupri-button>
                <cupri-button class="tools-close">Close</cupri-button>
              </div>
              <div class="toolpanel-body">
                <div class="tool-agents">
                  <div class="{{RowClass}}" data-tool-agent="{{Nick}}" data-repeat="ToolAgents">
                    <div class="tool-agent-nick">{{Nick}}</div>
                    <div class="tool-agent-count">{{Summary}}</div>
                  </div>
                </div>
                <cupri-virtual class="tool-list" height="440" item-height="46">
                  <div class="{{RowClass}}" data-tool="{{Name}}" data-repeat="ToolCatalog">
                    <div class="tool-line"><span class="tool-mark">{{Mark}}</span><span class="tool-name">{{Name}}</span><span class="tool-server">{{Server}}</span></div>
                    <div class="tool-desc">{{Description}}</div>
                  </div>
                </cupri-virtual>
              </div>
            </div>
          </div>
          <div class="roster">
            <div class="{{ToolsButtonClass}}" data-tools-open="1">Manage tools</div>
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
        .app { display: flex; flex-direction: row; height: 100%; }

        .sidebar { display: flex; flex-direction: column; width: 220px; background: #1b1e24; padding: 12px; }
        .brand { font-weight: bold; font-size: 16px; padding-bottom: 8px; }
        .status { font-size: 12px; padding: 4px 6px; border-radius: 4px; margin-bottom: 10px; }
        .status.on { color: #7fd88f; background: #16301c; }
        .status.off { color: #e88c8c; background: #341a1a; }
        .rooms { flex: 1; }
        .tab { display: flex; flex-direction: row; padding: 6px 8px; border-radius: 4px; cursor: pointer; }
        .tab.active { background: #2b313b; }
        .tab-name { flex: 1; }
        .badge { color: #14161a; background: #7fa7ff; border-radius: 8px; padding: 0 6px; font-size: 11px; }
        .badge.hidden { display: none; }
        .nick { font-size: 12px; color: #8b93a1; padding-top: 8px; }

        .browse { padding-top: 14px; }
        .browse.hidden { display: none; }
        .browse-title { font-size: 11px; color: #5d6572; padding-bottom: 4px; }
        .browse-row { display: flex; flex-direction: row; padding: 4px 8px; border-radius: 4px; cursor: pointer; }
        .browse-name { flex: 1; color: #8b93a1; font-size: 12px; }
        .browse-members { color: #5d6572; font-size: 11px; }

        .main { display: flex; flex-direction: column; flex: 1; }
        .header { display: flex; flex-direction: row; padding: 10px 14px; background: #1b1e24; }
        .room-name { font-weight: bold; padding-right: 12px; }
        .topic { color: #8b93a1; }

        .loadmore { padding: 6px 14px; color: #7fa7ff; font-size: 12px; background: #191c22; cursor: pointer; }
        .loadmore.hidden { display: none; }

        .timeline-menu { display: flex; flex-direction: column; flex: 1; }
        .timeline { flex: 1; padding: 6px 0; }
        /* No fixed height: rows wrap and the virtual list measures them (CupriFace 0.4.0). */
        .line { display: flex; flex-direction: row; padding: 3px 14px; }
        .time { color: #5d6572; font-size: 12px; width: 44px; }
        .sender { color: #7fa7ff; width: 110px; }
        .text { flex: 1; display: flex; flex-direction: column; }
        /* pre-wrap is load-bearing: messages carry hard newlines (agent replies are mostly
           paragraphs and code), and the CSS default would collapse them into one run-on line.
           Long lines still wrap. Requires CupriFace 0.5.0 or later.

           It is confined to the message body, and the row above is written on ONE line, because
           pre-wrap makes the markup's own indentation significant - a newline and two spaces
           between elements become visible whitespace in every message. */
        .body { white-space: pre-wrap; }
        .line.own .sender { color: #7fd88f; }
        .line.system { color: #8b93a1; font-style: italic; }
        .line.system .sender { color: #8b93a1; }
        .line.streaming .text { color: #cdd3dc; }

        .attach { color: #7fa7ff; background: #1f2530; border-radius: 4px; padding: 1px 6px; margin-left: 6px;
                  cursor: pointer; }
        .attach.hidden { display: none; }

        /* Width only: height follows the source's aspect ratio. Capped so one large screenshot
           cannot push the rest of the conversation off the screen - the chip above it still
           gives the real name and size, and clicking downloads the original. */
        .inline-image { width: 320px; margin-top: 4px; border-radius: 4px; }
        .inline-image.hidden { display: none; }

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

        .tools-open { font-size: 11px; color: #7fa7ff; background: #1f2530; border-radius: 4px;
                      padding: 4px 6px; margin-bottom: 12px; text-align: center; cursor: pointer; }
        .tools-open.hidden { display: none; }

        /* An overlay rather than another column: granting tools is a deliberate, occasional act,
           and it wants the width to show what each tool actually is. */
        .toolpanel { position: absolute; left: 0; top: 0; width: 100%; height: 100%;
                     display: flex; background: #0d0f13; padding: 40px 60px; }
        .toolpanel.hidden { display: none; }
        .toolpanel-inner { display: flex; flex-direction: column; flex: 1;
                           background: #14161a; border-radius: 6px; padding: 16px; }
        .toolpanel-head { display: flex; flex-direction: row; padding-bottom: 12px; }
        .toolpanel-title { flex: 1; font-weight: bold; }
        .toolpanel-status { color: #8b93a1; font-size: 12px; padding-right: 12px; }
        .tools-save { width: 80px; margin-right: 8px; background: #2f4a7a; color: #e6e8eb;
                      border: 1px solid #3d5f96; border-radius: 4px; }
        .tools-close { width: 80px; background: #232833; color: #e6e8eb;
                       border: 1px solid #333a46; border-radius: 4px; }
        .toolpanel-body { display: flex; flex-direction: row; flex: 1; }

        .tool-agents { width: 180px; padding-right: 12px; }
        .tool-agent { padding: 6px 8px; border-radius: 4px; margin-bottom: 6px; background: #1b1e24; cursor: pointer; }
        .tool-agent.active { background: #2b313b; }
        .tool-agent-nick { font-size: 13px; }
        .tool-agent-count { color: #8b93a1; font-size: 11px; }

        .tool-list { flex: 1; }
        .tool { padding: 5px 8px; border-radius: 4px; margin-bottom: 4px; background: #1b1e24; cursor: pointer; }
        /* Granted tools are marked as clearly as frontier agents are, and for the same reason:
           what an agent can reach is the thing an operator most needs to see at a glance. */
        .tool.granted { background: #16301c; }
        .tool-line { display: flex; flex-direction: row; }
        .tool-mark { width: 24px; color: #7fd88f; font-size: 11px; }
        .tool-name { flex: 1; font-size: 13px; }
        .tool-server { color: #8b93a1; font-size: 11px; }
        .tool-desc { color: #8b93a1; font-size: 11px; padding-left: 24px; }

        .composer-row { display: flex; flex-direction: row; padding: 10px 14px; background: #1b1e24; }
        /* Styled explicitly. The engine's defaults for a text field and a button are a white box
           and a light button, which on a dark app look like two controls that failed to load. */
        .composer { flex: 1; min-height: 44px; max-height: 120px; background: #14161a; color: #e6e8eb;
                    border: 1px solid #2b313b; border-radius: 4px; padding: 6px 8px; caret-color: #7fa7ff; }
        .composer:focus { border: 1px solid #4a5a75; }
        .voice-row { display: flex; flex-direction: row; padding: 0 14px 8px 14px; background: #1b1e24; }
        .voice-row.hidden { display: none; }
        .voice-status { flex: 1; color: #8b93a1; font-size: 12px; }
        /* The gate's state, said in colour as well as in words: a room microphone is watched
           from across a desk, where the label is too small to read. */
        .mic.armed { background: #2b3a4a; }
        .mic.hearing { background: #2f6b3f; }
        .mic.working { background: #6b5a2f; }
        .mic.hidden { display: none; }
        .readback.hidden { display: none; }
        .send { width: 90px; margin-left: 8px; background: #2f4a7a; color: #e6e8eb;
                border: 1px solid #3d5f96; border-radius: 4px; }
        .mic { margin-right: 8px; min-width: 64px; background: #232833; color: #e6e8eb;
               border: 1px solid #333a46; border-radius: 4px; }
        .readback { background: #232833; color: #8b93a1; border: 1px solid #333a46;
                    border-radius: 4px; font-size: 12px; }
        """;

    public override void Configure(CupriDocument doc)
    {
        _doc = doc;

        doc.OnClick(".send", _ => Send());
        doc.OnClick(".mic", _ => ToggleVoice());
        doc.OnClick(".readback", _ => CycleReadback());

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

        // Right-click menu items. CupriFace opens the menu at the pointer and leaves the
        // clipboard to the host, so every one of these ends in a call through IClipboard.
        doc.OnClick(".copy-selection", _ => CopySelection());
        doc.OnClick(".copy-image", _ => CopyMostRecentImage());
        doc.OnClick(".copy-room", _ => Clipboard.SetText(ViewModel.Model.ActiveRoom));

        // The engine's own Cut/Copy/Paste menu over a text field raises this instead of showing
        // one of ours; it does not touch the clipboard itself.
        doc.ContextRequested += command =>
        {
            switch (command)
            {
                case CupriFace.Interaction.ContextCommand.Copy:
                    CopySelection();
                    break;
                case CupriFace.Interaction.ContextCommand.Cut:
                    var cut = doc.CutSelection() ?? "";
                    if (cut.Length > 0)
                    {
                        Clipboard.SetText(cut);
                    }

                    break;
            }
        };

        // Ctrl+C with a selection, for people who never open a menu.
        doc.OnShortcut(KeyMods.Ctrl, "C", CopySelection);

        doc.OnAction("data-join", e =>
        {
            if (string.IsNullOrEmpty(e.Value))
            {
                return false;
            }

            _ = JoinRoomAsync(e.Value);
            return true;
        });

        doc.OnAction("data-tools-open", _ =>
        {
            OpenTools();
            return true;
        });

        doc.OnAction("data-tool-agent", e =>
        {
            if (string.IsNullOrEmpty(e.Value))
            {
                return false;
            }

            var agent = e.Value;
            ViewModel.Post(() => ViewModel.SelectToolAgent(agent));
            _ = ToolsOpenAsync(agent);
            return true;
        });

        doc.OnAction("data-tool", e =>
        {
            if (string.IsNullOrEmpty(e.Value))
            {
                return false;
            }

            var tool = e.Value;
            ViewModel.Post(() => ViewModel.ToggleTool(tool));
            return true;
        });

        doc.OnClick(".tools-save", _ => SaveTools());
        doc.OnClick(".tools-close", _ => ViewModel.Post(() => ViewModel.ShowToolPanel(false)));

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

    /// <summary>
    /// Copy the current selection. Falls back to the newest message when nothing is selected,
    /// because "Copy" with an empty selection doing nothing at all reads as a broken menu.
    /// </summary>
    public void CopySelection()
    {
        var selected = _doc?.CopySelection() ?? "";
        if (selected.Length == 0)
        {
            selected = ViewModel.Model.Messages.LastOrDefault()?.Text ?? "";
        }

        if (selected.Length > 0)
        {
            Clipboard.SetText(selected);
        }
    }

    /// <summary>
    /// Copy the most recently shown image in this room. Where the platform cannot put a bitmap on
    /// the clipboard, its path goes on as text instead — pasteable somewhere useful rather than
    /// nothing happening.
    /// </summary>
    public void CopyMostRecentImage()
    {
        var image = ViewModel.Model.Messages.LastOrDefault(m => m.ImageSrc.Length > 0);
        if (image is null)
        {
            return;
        }

        var path = new Uri(image.ImageSrc).LocalPath;
        if (!Clipboard.TrySetImage(path))
        {
            Clipboard.SetText(path);
        }
    }

    /// <summary>
    /// Open the tool-grants panel and ask the head to fill it. Tools run on the server, so
    /// everything this panel does is a request the server is free to refuse.
    /// </summary>
    public void OpenTools()
    {
        ViewModel.Post(() => ViewModel.ShowToolPanel(true));
        _ = ToolsOpenAsync(ViewModel.Model.ToolsAgent);
    }

    /// <summary>Send the selected agent's complete grant set to the server.</summary>
    public void SaveTools()
    {
        var agent = ViewModel.Model.ToolsAgent;
        if (agent.Length == 0)
        {
            ViewModel.Post(() => ViewModel.ToolGrantsFailed("Pick an agent first."));
            return;
        }

        _ = ToolsSaveAsync(agent, ViewModel.PendingGrants);
    }

    /// <summary>
    /// Open or close the microphone. The view model is not updated here: the session reports what
    /// actually happened through <c>SetVoiceState</c>, so a backend that failed to open a device
    /// does not leave a button claiming to be listening.
    /// </summary>
    public void ToggleVoice()
    {
        if (!ViewModel.VoiceAvailable)
        {
            return;
        }

        _ = VoiceToggleAsync(!ViewModel.Listening);
    }

    /// <summary>Cycle the readback policy and tell the head, which owns the speaking side.</summary>
    public void CycleReadback()
    {
        ViewModel.Post(() =>
        {
            var policy = ViewModel.CycleReadback();
            _ = ReadbackChangedAsync(policy);
        });
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
