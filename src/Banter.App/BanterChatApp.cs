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
    /// The native file dialog. Defaults to one that cannot pick, so the app runs headlessly and
    /// the attach control stays hidden until a head wires a real one.
    /// </summary>
    public IFilePicker FilePicker { get; init; } = NullFilePicker.Instance;

    /// <summary>Called with a chosen path, to be sent to the room the user is looking at.</summary>
    public Func<string, string, Task> AttachAsync { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>
    /// Called with the server, account and password from the connect screen. Only a head that
    /// shows that screen wires this — a desktop head is given all three before the window opens.
    /// </summary>
    public Func<string, string, string, Task> ConnectAsync { get; init; } = (_, _, _) => Task.CompletedTask;

    /// <summary>Rewrites a message already sent. Only the author may; the server enforces it.</summary>
    public Func<string, string, string, Task> EditAsync { get; init; } = (_, _, _) => Task.CompletedTask;

    /// <summary>Takes a message back. The author may remove their own, an admin anyone's.</summary>
    public Func<string, string, Task> DeleteAsync { get; init; } = (_, _) => Task.CompletedTask;

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
    /// Whether closing the window leaves the client running in the tray. Off unless a head asks
    /// for it: it changes what the close button means, and a head whose platform has no tray
    /// would otherwise hide a window that nothing can bring back.
    /// </summary>
    public bool StayInTray { get; init; }

    public override bool CloseToTray => StayInTray;

    public override string TrayCloseLabel => "Quit Banter";

    /// <summary>
    /// The same colour the stylesheet paints the page. The host clears to this before the first
    /// frame and during a resize, so matching it removes the white flash on open and the white
    /// edge that trails a window being dragged wider.
    /// </summary>
    public override SkiaSharp.SKColor Background => new(0x0b, 0x0d, 0x10);

    /// <summary>
    /// Drives the pump in <see cref="Present"/>. Messages arrive on socket threads with no input
    /// event to piggyback on, so the host must tick even when the user is idle. 20 Hz is well
    /// inside the measured per-delta cost (~0.8 ms) and cheap when nothing has changed.
    /// </summary>
    public override double RefreshIntervalSeconds => 0.05;

    public override string Html => """
        <div class="app">
          <div class="rail">
            <div class="logo">B</div>
            <div class="{{ToolsButtonClass}}" data-tools-open="1">T</div>
          </div>
          <div class="sidebar">
            <div class="workspace">
              <div class="workspace-name">Banter</div>
              <div class="{{StatusClass}}">{{Status}}</div>
            </div>
            <div class="sidebar-scroll">
              <div class="section-title">Rooms</div>
              <div class="rooms">
                <div class="{{TabClass}}" data-repeat="Rooms" data-room="{{Name}}">
                  <span class="hash">#</span><span class="tab-name">{{Label}}</span><span class="{{BadgeClass}}">{{Badge}}</span>
                </div>
                <div class="{{BrowseClass}}">
                  <div class="section-title">Other rooms</div>
                  <div class="browse-row" data-repeat="Browse" data-join="{{Name}}">
                    <span class="hash">#</span><span class="browse-name">{{Label}}</span><span class="browse-members">{{Members}}</span>
                  </div>
                </div>
              </div>
            </div>
            <div class="sidebar-footer">
              <div class="me">{{NickInitials}}</div>
              <div class="identity"><span class="nick">{{Nick}}</span><span class="identity-sub">{{Dispatch}}</span></div>
            </div>
          </div>
          <div class="main">
            <div class="header">
              <div class="header-title">
                <span class="room-name">{{ActiveRoom}}</span>
                <span class="topic">{{Topic}}</span>
              </div>
              <span class="dispatch">{{Dispatch}}</span>
            </div>
            <div class="{{LoadOlderClass}}" data-load-older="1">{{LoadOlderText}}</div>
            <cupri-context-menu class="timeline-menu">
              <cupri-virtual class="timeline" height="620" item-height="52" anchor="bottom">
                <div class="{{RowClass}}" data-repeat="Messages" data-msg="{{Id}}">
                  <span class="pfp">{{Initials}}</span>
                  <span class="msg-main">
                    <span class="msg-head"><span class="sender">{{Sender}}</span><span class="time">{{Time}}</span><span class="edited">{{EditedMark}}</span></span>
                    <span class="text"><span class="body">{{Text}}</span><span class="{{AttachClass}}" data-file="{{FileId}}">{{AttachText}}</span><cupri-image class="{{ImageClass}}" src="{{ImageSrc}}" alt="{{AttachText}}"></cupri-image></span>
                  </span>
                </div>
              </cupri-virtual>
              <cupri-menu-item class="{{EditItemClass}}">Edit message</cupri-menu-item>
              <cupri-menu-item class="{{DeleteItemClass}}">Delete message</cupri-menu-item>
              <cupri-menu-item class="copy-selection">Copy</cupri-menu-item>
              <cupri-menu-item class="copy-image">Copy image</cupri-menu-item>
              <cupri-menu-item class="copy-room">Copy room name</cupri-menu-item>
            </cupri-context-menu>
            <div class="{{EditingClass}}">editing a message — Esc to cancel</div>
            <div class="composer-wrap">
              <div class="composer-row">
                <span class="prompt">&gt;</span>
                <cupri-textarea class="composer" value="{{Composer}}" placeholder="Message"></cupri-textarea>
                <cupri-button class="{{MicClass}}">{{MicText}}</cupri-button>
                <cupri-button class="{{AttachButtonClass}}">Attach</cupri-button>
                <cupri-button class="send">Send</cupri-button>
              </div>
              <div class="composer-hint">/help for commands · @name to reach an agent directly</div>
            </div>
            <div class="{{VoiceRowClass}}">
              <span class="voice-status">{{VoiceStatus}}</span>
              <cupri-button class="{{ReadbackClass}}">{{ReadbackText}}</cupri-button>
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
              <div class="agent-row">
                <span class="agent-pfp">{{Initials}}</span>
                <span class="agent-main">
                  <span class="agent-line"><span class="agent-nick">{{Nick}}</span><span class="agent-role">{{Role}}</span></span>
                  <span class="agent-meta">{{Locality}} · {{Skills}}</span>
                </span>
              </div>
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
          <div class="{{ConnectClass}}">
            <div class="connect-card">
              <div class="connect-title">Banter</div>
              <div class="connect-label">Server</div>
              <cupri-textfield class="connect-field" value="{{ConnectServer}}" placeholder="tcp://host:7770"></cupri-textfield>
              <div class="connect-label">Name</div>
              <cupri-textfield class="connect-field" value="{{ConnectUser}}" placeholder="your nick"></cupri-textfield>
              <div class="connect-label">Password</div>
              <cupri-password class="connect-field" value="{{ConnectPassword}}"></cupri-password>
              <cupri-button class="connect-go">{{ConnectButtonText}}</cupri-button>
              <div class="connect-status">{{ConnectStatus}}</div>
            </div>
          </div>
        </div>
        """;

    public override string Css => """
        /* The cupri-* components read these rather than inheriting `color`, so setting them here
           is what makes typed text in a field the same brightness as the text around it. Without
           them the components fall back to their own defaults — which, on this dark ground, drew
           entered values DIMMER than the placeholder they replaced. */
        body { background: #0b0d10; color: #f3f5f7; font-size: 14px;
               --cupri-text: #f3f5f7; --cupri-muted: #8d97a6; --cupri-surface: #151920;
               --cupri-border: #252b35; --cupri-hover: #1b2029; }
        .app { display: flex; flex-direction: row; height: 100%; background: #0b0d10; }

        /* Rail: what the app is, and who you are. Rooms live in the sidebar, so this stays
           deliberately thin — it is orientation, not navigation. */
        .rail { display: flex; flex-direction: column; align-items: center; width: 72px;
                padding: 14px 10px; background: #0c0f14; border-right: 1px solid #20262f; }
        .logo { width: 44px; height: 44px; border-radius: 14px; display: flex;
                align-items: center; justify-content: center; font-weight: bold; font-size: 18px;
                background: linear-gradient(145deg, #ef4444, #991b1b); color: #ffffff;
                box-shadow: 0 8px 24px #ef444433; }
        /* Who you are is said once, in the sidebar footer beside your name. The rail carries
           what the app is and what it can open, and nothing that only repeats what is next to it. */
        .me { width: 34px; height: 34px; border-radius: 17px; display: flex;
              align-items: center; justify-content: center; font-size: 12px; font-weight: bold;
              background: linear-gradient(145deg, #334155, #1e293b); border: 1px solid #3d4653;
              color: #e2e8f0; }

        .sidebar { display: flex; flex-direction: column; width: 248px; background: #0e1116;
                   border-right: 1px solid #20262f; }
        .workspace { height: 66px; padding: 0 16px; display: flex; flex-direction: column;
                     justify-content: center; border-bottom: 1px solid #20262f; }
        .workspace-name { font-size: 14px; font-weight: bold; }
        .status { font-size: 11px; margin-top: 3px; }
        .status.on { color: #34d399; }
        .status.off { color: #fb7185; }
        .sidebar-scroll { flex: 1; overflow: scroll; padding: 12px 10px 18px 10px; }
        .section-title { padding: 0 9px 6px 9px; color: #717c8d; font-size: 10px; font-weight: bold; }
        .rooms { display: flex; flex-direction: column; }
        .tab { display: flex; flex-direction: row; align-items: center; height: 36px;
               padding: 0 10px; border-radius: 9px; cursor: pointer; color: #aeb6c2; }
        .tab:hover { background: #171b22; color: #e5e7eb; }
        .tab.active { background: #202530; color: #ffffff; }
        .hash { color: #667085; font-weight: bold; padding-right: 9px; }
        .tab.active .hash { color: #fb7185; }
        .tab-name { flex: 1; }
        .badge { min-width: 18px; height: 18px; padding: 0 5px; border-radius: 9px;
                 display: flex; align-items: center; justify-content: center;
                 font-size: 10px; font-weight: bold; background: #4c1d1d; color: #fecaca; }
        .badge.hidden { display: none; }

        .browse { padding-top: 14px; }
        .browse.hidden { display: none; }
        .browse-row { display: flex; flex-direction: row; align-items: center; height: 32px;
                      padding: 0 10px; border-radius: 9px; cursor: pointer; }
        .browse-row:hover { background: #171b22; }
        .browse-name { flex: 1; color: #8d97a6; font-size: 12px; }
        .browse-members { color: #5d6572; font-size: 11px; }

        .sidebar-footer { display: flex; flex-direction: row; align-items: center;
                          border-top: 1px solid #20262f; padding: 12px; background: #0b0e12; }
        .identity { display: flex; flex-direction: column; padding-left: 10px; }
        .nick { font-size: 12px; font-weight: bold; }
        .identity-sub { font-size: 10px; color: #8d97a6; }

        .main { display: flex; flex-direction: column; flex: 1; background: #111419; }
        .header { height: 66px; display: flex; flex-direction: row; align-items: center;
                  padding: 0 18px; border-bottom: 1px solid #20262f; }
        .header-title { display: flex; flex-direction: column; flex: 1; }
        .room-name { font-size: 14px; font-weight: bold; }
        .topic { color: #8d97a6; font-size: 11px; margin-top: 2px; }
        .dispatch { color: #8d97a6; font-size: 11px; }

        .loadmore { padding: 6px 18px; color: #fb7185; font-size: 12px; cursor: pointer; }
        .loadmore.hidden { display: none; }

        .timeline-menu { display: flex; flex-direction: column; flex: 1; }
        .timeline { flex: 1; padding: 12px 0; }
        /* No fixed height: rows wrap and the virtual list measures them (CupriFace 0.4.0). */
        .line { display: flex; flex-direction: row; padding: 5px 18px; }
        .line:hover { background: #ffffff08; }
        /* An avatar rather than a column of nicks: a room is read by scanning down the left edge,
           and a letterform is recognisable at a glance where a name has to be read. */
        .pfp { width: 36px; height: 36px; border-radius: 11px; display: flex;
               align-items: center; justify-content: center; font-size: 11px; font-weight: bold;
               background: #262d38; border: 1px solid #384150; color: #cbd5e1; }
        .msg-main { display: flex; flex-direction: column; flex: 1; padding-left: 10px; }
        .msg-head { display: flex; flex-direction: row; align-items: center; }
        .sender { color: #f8fafc; font-size: 12px; font-weight: bold; }
        .time { color: #596474; font-size: 9px; padding-left: 8px; }
        .text { display: flex; flex-direction: column; }
        /* pre-wrap is load-bearing: messages carry hard newlines (agent replies are mostly
           paragraphs and code), and the CSS default would collapse them into one run-on line.
           Long lines still wrap. Requires CupriFace 0.5.0 or later.

           It is confined to the message body, and the row above is written on ONE line, because
           pre-wrap makes the markup's own indentation significant - a newline and two spaces
           between elements become visible whitespace in every message. */
        .body { white-space: pre-wrap; font-size: 13px; color: #c8ced7; margin-top: 2px; }
        .line.own .sender { color: #34d399; }
        /* No author, so no avatar: the line sits where the text would start instead. */
        .line.system { color: #748094; font-style: italic; padding-left: 64px; }
        .line.system .sender { color: #8d97a6; }
        .line.system .pfp { display: none; }
        .line.system .msg-main { padding-left: 0; }
        .line.streaming .body { color: #cdd3dc; }

        .attach { color: #fb7185; background: #1f2530; border-radius: 5px; padding: 1px 6px;
                  margin-top: 4px; cursor: pointer; }
        .attach.hidden { display: none; }

        /* Width only: height follows the source's aspect ratio. Capped so one large screenshot
           cannot push the rest of the conversation off the screen - the chip above it still
           gives the real name and size, and clicking downloads the original. */
        .inline-image { width: 320px; margin-top: 4px; border-radius: 6px; }
        .inline-image.hidden { display: none; }

        /* Quiet, because it is a footnote to the message rather than part of it. */
        .edited { color: #6b7280; font-size: 10px; padding-left: 8px; }
        /* A taken-back message keeps its place in the conversation and loses its content: the
           gap would otherwise make a reply above it look like a reply to nothing. */
        .line.deleted .body { color: #6b7280; font-style: italic; }

        /* An egress notice must not read like ordinary chatter. */
        .line.egress { background: #33261a; }
        .line.egress .body { color: #e0a56a; }
        .line.egress .sender { color: #e0a56a; }

        .editing-banner { padding: 6px 18px; background: #22304a; color: #b9c6da; font-size: 12px; }
        .editing-banner.hidden { display: none; }
        .menu-edit.hidden, .menu-delete.hidden { display: none; }

        .composer-wrap { padding: 8px 16px 12px 16px; }
        .composer-row { display: flex; flex-direction: row; align-items: center;
                        border: 1px solid #303744; background: #11151b; border-radius: 14px;
                        padding: 6px 8px; box-shadow: 0 18px 55px #00000052; }
        .prompt { color: #fb7185; font-weight: bold; padding: 0 9px 0 3px; }
        /* Styled explicitly. The engine's defaults for a text field and a button are a white box
           and a light button, which on a dark app look like two controls that failed to load. */
        .composer { flex: 1; min-height: 22px; max-height: 110px; background: #11151b;
                    color: #f3f5f7; border: 0; padding: 3px 0; caret-color: #fb7185; }
        .composer-hint { font-size: 10px; color: #5f6877; margin: 7px 4px 0 4px; }

        .mic { margin-left: 8px; min-width: 64px; height: 32px; padding: 0 14px; font-size: 13px; background: #1b2029; color: #f3f5f7;
               border: 1px solid #333a46; border-radius: 10px; line-height: 32px; }
        /* The gate's state, said in colour as well as in words: a room microphone is watched
           from across a desk, where the label is too small to read. */
        .mic.armed { background: #2b3a4a; }
        .mic.hearing { background: #2f6b3f; }
        .mic.working { background: #6b5a2f; }
        .mic.hidden { display: none; }
        .attach-open { margin-left: 8px; min-width: 66px; height: 32px; padding: 0 14px; font-size: 13px; background: #1b2029; color: #f3f5f7;
                       border: 1px solid #333a46; border-radius: 10px; line-height: 32px; }
        .attach-open.hidden { display: none; }
        .send { min-width: 62px; height: 32px; padding: 0 14px; font-size: 13px; margin-left: 8px; background: #ef4444; color: #ffffff;
                border: 0; border-radius: 10px; font-weight: bold; line-height: 32px; }

        .voice-row { display: flex; flex-direction: row; padding: 0 18px 10px 18px; }
        .voice-row.hidden { display: none; }
        .voice-status { flex: 1; color: #8d97a6; font-size: 12px; }
        .readback { height: 30px; background: #1b2029; color: #8d97a6; border: 1px solid #333a46;
                    border-radius: 10px; font-size: 12px; line-height: 30px; }
        .readback.hidden { display: none; }

        .roster { width: 236px; background: #0e1116; border-left: 1px solid #20262f;
                  padding: 12px 10px; overflow: scroll; }
        .roster-title { font-size: 10px; font-weight: bold; color: #717c8d; padding: 4px 8px 8px 8px; }
        .tasks { padding-bottom: 12px; }
        .tasks.hidden { display: none; }
        .task { padding: 7px 8px; border-radius: 9px; margin-bottom: 6px; background: #151920; }
        /* Held work reads differently from work still waiting for someone. */
        .task.held { background: #1f2b33; }
        .task-title { font-size: 12px; }
        .task-status { color: #8d97a6; font-size: 11px; }

        .agent { padding: 7px 8px; border-radius: 9px; margin-bottom: 4px; }
        .agent:hover { background: #171b22; }
        /* Frontier agents are marked, not merely listed: whether a third party is in the room is
           the thing a human most needs to be able to see at a glance. */
        .agent.frontier { background: #33261a; }
        .agent.delegator { background: #16301c; }
        .agent-row { display: flex; flex-direction: row; align-items: center; }
        .agent-pfp { width: 30px; height: 30px; border-radius: 15px; display: flex;
                     align-items: center; justify-content: center; font-size: 10px; font-weight: bold;
                     background: linear-gradient(145deg, #334155, #1e293b); border: 1px solid #3d4653;
                     color: #e2e8f0; }
        .agent-main { display: flex; flex-direction: column; flex: 1; padding-left: 9px; }
        .agent-line { display: flex; flex-direction: row; align-items: center; }
        .agent-nick { flex: 1; font-size: 11px; font-weight: bold; }
        .agent-role { color: #34d399; font-size: 9px; }
        .agent-meta { color: #8d97a6; font-size: 9px; margin-top: 1px; }
        .agent.frontier .agent-meta { color: #e0a56a; }

        .tools-open { width: 42px; height: 42px; border-radius: 14px; display: flex;
                      align-items: center; justify-content: center; font-size: 12px;
                      font-weight: bold; background: #1b2029; color: #bec5cf; cursor: pointer; }
        .tools-open:hover { background: #242a34; color: #ffffff; }
        .tools-open.hidden { display: none; }

        /* An overlay rather than another column: granting tools is a deliberate, occasional act,
           and it wants the width to show what each tool actually is. */
        .toolpanel { position: absolute; left: 0; top: 0; width: 100%; height: 100%;
                     display: flex; background: #0b0d10; }
        .toolpanel.hidden { display: none; }
        /* The inset lives here, not on the overlay: `width: 100%` plus padding overflows, because
           the engine sizes content-box and does not honour `box-sizing` (CupriFace#76). */
        .toolpanel-inner { display: flex; flex-direction: column; flex: 1; margin: 36px 56px;
                           background: #151920; border-radius: 14px; padding: 16px; }
        .toolpanel-head { display: flex; flex-direction: row; padding-bottom: 12px; }
        .toolpanel-title { flex: 1; font-weight: bold; }
        .toolpanel-status { color: #8d97a6; font-size: 12px; padding-right: 12px; }
        .tools-save { min-width: 72px; height: 32px; padding: 0 14px; font-size: 13px; margin-right: 8px; background: #ef4444; color: #ffffff;
                      border: 0; border-radius: 10px; line-height: 32px; }
        .tools-close { min-width: 72px; height: 32px; padding: 0 14px; font-size: 13px; background: #1b2029; color: #f3f5f7;
                       border: 1px solid #333a46; border-radius: 10px; line-height: 32px; }
        .toolpanel-body { display: flex; flex-direction: row; flex: 1; }

        .tool-agents { width: 180px; padding-right: 12px; }
        .tool-agent { padding: 7px 8px; border-radius: 9px; margin-bottom: 6px;
                      background: #111419; cursor: pointer; }
        .tool-agent.active { background: #202530; }
        .tool-agent-nick { font-size: 13px; }
        .tool-agent-count { color: #8d97a6; font-size: 11px; }

        .tool-list { flex: 1; }
        .tool { padding: 6px 8px; border-radius: 9px; margin-bottom: 4px; background: #111419;
                cursor: pointer; }
        /* Granted tools are marked as clearly as frontier agents are, and for the same reason:
           what an agent can reach is the thing an operator most needs to see at a glance. */
        .tool.granted { background: #16301c; }
        .tool-line { display: flex; flex-direction: row; }
        .tool-mark { width: 24px; color: #34d399; font-size: 11px; }
        .tool-name { flex: 1; font-size: 13px; }
        .tool-server { color: #8d97a6; font-size: 11px; }
        .tool-desc { color: #8d97a6; font-size: 11px; padding-left: 24px; }

        /* Over everything, and its own colour: until this is dealt with there is no room to
           look at behind it. */
        /* No padding here, and the offset is on the card instead. `width: 100%` plus padding
           overflows, because the engine sizes content-box and does not honour `box-sizing`
           (CupriFace#76) — which put the centre 60px right of the viewport's. */
        .connect { position: absolute; left: 0; top: 0; width: 100%; height: 100%;
                   display: flex; justify-content: center; background: #0b0d10; }
        .connect.hidden { display: none; }
        /* Centred by the container, not by auto margins on the item: the engine does not honour
           `margin: auto` on a flex item either. Top-aligned on purpose — a short viewport must
           not push the card off-screen. */
        .connect-card { width: 360px; margin-top: 60px;
                        display: flex; flex-direction: column; background: #151920;
                        border-radius: 14px; padding: 20px; height: 252px;
                        box-shadow: 0 18px 55px #00000052; }
        .connect-title { font-weight: bold; font-size: 20px; padding-bottom: 12px; }
        .connect-label { font-size: 11px; color: #8d97a6; padding-bottom: 4px; padding-top: 8px; }
        .connect-field { background: #0d1014; color: #f3f5f7; border: 1px solid #252b35;
                         border-radius: 10px; padding: 7px 10px; }
        .connect-go { margin-top: 16px; height: 36px; padding: 0; font-size: 13px; text-align: center; background: #ef4444; color: #ffffff; border: 0;
                      border-radius: 10px; font-weight: bold; line-height: 36px; }
        .connect-status { font-size: 12px; color: #fb7185; padding-top: 10px; }
        """;

    public override void Configure(CupriDocument doc)
    {
        _doc = doc;

        doc.OnClick(".send", _ => Send());
        doc.OnClick(".mic", _ => ToggleVoice());
        doc.OnClick(".readback", _ => CycleReadback());
        doc.OnClick(".attach-open", _ => PickAttachment());
        doc.OnClick(".connect-go", _ => Connect());

        // Ctrl+Enter would send, and Escape would abandon an edit; plain Enter stays a newline,
        // which is what a multi-line composer needs.
        //
        // Neither of these fires yet, and the composer's placeholder no longer promises one of
        // them. OnShortcut only matches single-character text, while Enter and Escape reach the
        // document as an EditKey with no text at all, so the registration is dead on arrival
        // (CupriFace#88). Left registered: they start working the moment that does, and a binding
        // that is merely early is better than a feature nobody remembers to add back.
        doc.OnShortcut(KeyMods.Ctrl, "Enter", Send);
        doc.OnShortcut(KeyMods.None, "Escape", CancelEdit);

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
        // Which message the menu will act on, from the element the right-click or long-press
        // actually landed on (CupriFace#85). e.Value, not e.Model: the attribute carries the id,
        // while Model is the ROOT model — data-repeat discards each item once its bindings are
        // substituted, so there is no row object to hand back.
        doc.OnContext("data-msg", e =>
        {
            SetContextMessage(e.Value);
            return false;
        });

        doc.OnClick(".menu-edit", _ => BeginEdit());
        doc.OnClick(".menu-delete", _ => DeleteContextMessage());
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

    /// <summary>
    /// Open the file dialog and send whatever comes back.
    ///
    /// <para>The room is captured before the dialog opens. A dialog is modal to the user but not
    /// to the app, and sending to whichever room happened to be on screen when they finally
    /// clicked Open would put the file somewhere they were not looking when they chose it.</para>
    /// </summary>
    public void PickAttachment()
    {
        if (!FilePicker.IsSupported)
        {
            return;
        }

        var room = ViewModel.Model.ActiveRoom;
        if (room.Length == 0)
        {
            return;
        }

        _ = PickAndSendAsync(room);
    }

    private async Task PickAndSendAsync(string room)
    {
        var path = await FilePicker.PickAsync($"Send a file to {room}").ConfigureAwait(false);
        if (path is { Length: > 0 })
        {
            await AttachAsync(room, path).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Take what the connect form holds and hand it to the head.
    ///
    /// <para>Read on the render thread, where the bindings are written, and then passed by value —
    /// the head must not reach back into the model for them, and the password is cleared out of it
    /// as soon as the attempt resolves.</para>
    /// </summary>
    public void Connect()
    {
        ViewModel.Post(() =>
        {
            if (ViewModel.Model.ConnectButtonText == "Connecting")
            {
                return;                                     // already trying; a second tap is a slip
            }

            if (!ViewModel.TryReadConnect(out var server, out var user, out var password))
            {
                return;
            }

            ViewModel.Connecting();
            _ = ConnectAsync(server, user, password);
        });
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

    /// <summary>
    /// Remembers the message under the pointer, and decides which menu items suit it. Edit is
    /// offered only over your own: the server refuses anybody else, and a menu item that always
    /// fails is worse than one that is not there.
    /// </summary>
    private void SetContextMessage(string? messageId)
    {
        var row = ViewModel.FindMessage(ViewModel.Model.ActiveRoom, messageId ?? "");
        _contextMessage = row;

        var actionable = row is not null && row.Id.Length > 0 && !row.RowClass.Contains("deleted", StringComparison.Ordinal);
        var mine = actionable && string.Equals(row!.Sender, ViewModel.Model.Nick, StringComparison.OrdinalIgnoreCase);

        ViewModel.Model.EditItemClass = mine ? "menu-edit" : "menu-edit hidden";
        ViewModel.Model.DeleteItemClass = actionable ? "menu-delete" : "menu-delete hidden";
    }

    /// <summary>
    /// Loads the message into the composer to be rewritten. The composer rather than an overlay:
    /// it is the one place this app types, and a second editor would need its own keyboard, IME
    /// and paste handling to no benefit.
    /// </summary>
    private void BeginEdit()
    {
        if (_contextMessage is not { Id.Length: > 0 } row)
        {
            return;
        }

        ViewModel.Model.EditingId = row.Id;
        ViewModel.Model.Composer = row.Text;
        ViewModel.Model.EditingClass = "editing-banner";
        _doc?.Refresh();
    }

    /// <summary>Abandons an edit, leaving the message as it was.</summary>
    public void CancelEdit()
    {
        if (ViewModel.Model.EditingId.Length == 0)
        {
            return;
        }

        ViewModel.Model.EditingId = "";
        ViewModel.Model.Composer = "";
        ViewModel.Model.EditingClass = "editing-banner hidden";
        _doc?.Refresh();
    }

    private void DeleteContextMessage()
    {
        if (_contextMessage is not { Id.Length: > 0 } row)
        {
            return;
        }

        // If it was the one being edited, that edit no longer has a subject.
        if (ViewModel.Model.EditingId == row.Id)
        {
            CancelEdit();
        }

        _ = DeleteAsync(ViewModel.Model.ActiveRoom, row.Id);
    }

    /// <summary>The message the right-click menu is aimed at, set as the click lands.</summary>
    private MessageRow? _contextMessage;

    /// <summary>Send the composer's contents to the active room and clear it.</summary>
    public void Send()
    {
        var text = ViewModel.Model.Composer.Trim();
        var room = ViewModel.Model.ActiveRoom;
        if (text.Length == 0 || room.Length == 0)
        {
            return;
        }

        // An edit in progress takes the composer's contents instead of the room doing so.
        if (ViewModel.Model.EditingId is { Length: > 0 } editing)
        {
            ViewModel.Model.EditingId = "";
            ViewModel.Model.Composer = "";
            ViewModel.Model.EditingClass = "editing-banner hidden";
            _doc?.Refresh();
            _ = EditAsync(room, editing, text);
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
