using Banter.Protocol;
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
    /// The agents page (admin only). A head that wires these gets agent management; one that does
    /// not simply never shows the button, because the server would refuse a non-admin anyway.
    /// </summary>
    public Func<Task> AgentsListAsync { get; init; } = () => Task.CompletedTask;

    /// <summary>Creates an identity. The head reports the enrolment code back through the model.</summary>
    public Func<string, IReadOnlyList<string>, IReadOnlyList<string>, AgentLocality, DataSensitivity, Task>
        AgentCreateAsync { get; init; } = (_, _, _, _, _) => Task.CompletedTask;

    /// <summary>A fresh code for a new machine, retiring the key the old one holds.</summary>
    public Func<string, Task> AgentReissueAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Removes an identity. Its key stops working at once.</summary>
    public Func<string, Task> AgentRemoveAsync { get; init; } = _ => Task.CompletedTask;

    public Func<Task> UsersListAsync { get; init; } = () => Task.CompletedTask;

    /// <summary>(username, isAdmin) — the reply's temporary password is the host's to show.</summary>
    public Func<string, bool, Task> UserCreateAsync { get; init; } = (_, _) => Task.CompletedTask;

    public Func<string, Task> UserResetAsync { get; init; } = _ => Task.CompletedTask;

    public Func<string, bool, Task> UserSetAdminAsync { get; init; } = (_, _) => Task.CompletedTask;

    public Func<string, Task> UserRemoveAsync { get; init; } = _ => Task.CompletedTask;

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
            <div class="{{AdminButtonClass}}" data-admin-open="1">A</div>
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
              <div class="{{MentionsClass}}">
                <div class="{{RowClass}}" data-repeat="Mentions" data-mention="{{Nick}}">
                  <span class="mention-pfp">{{Initials}}</span><span class="mention-nick">{{Nick}}</span><span class="mention-meta">{{Meta}}</span>
                </div>
                <div class="mention-hint">Ctrl+Up / Ctrl+Down to choose · Enter to insert</div>
              </div>
              <div class="composer-row">
                <span class="prompt">&gt;</span>
                <cupri-textarea class="composer" value="{{Composer}}" placeholder="Message" data-composer="1" submit-on-enter></cupri-textarea>
                <cupri-button class="{{MicClass}}">{{MicText}}</cupri-button>
                <cupri-button class="{{AttachButtonClass}}">Attach</cupri-button>
                <cupri-button class="send">Send</cupri-button>
              </div>
              <div class="composer-hint">Enter to send · Shift+Enter for a newline · /help for commands · @name to reach an agent directly</div>
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
          <div class="{{AdminClass}}">
            <div class="adminpanel-inner">
              <div class="toolpanel-head">
                <span class="{{AdminTabAgentsClass}}" data-admin-tab="agents">Agents</span>
                <span class="{{AdminTabUsersClass}}" data-admin-tab="users">Users</span>
                <span class="toolpanel-status">{{AdminStatus}}</span>
                <cupri-button class="admin-close">Close</cupri-button>
              </div>
              <div class="{{AdminCodeClass}}">
                <div class="admin-code-note">{{AdminCodeFor}}</div>
                <div class="admin-code-row">
                  <span class="admin-code-value">{{AdminCode}}</span>
                  <cupri-button class="admin-copy">Copy</cupri-button>
                </div>
              </div>
              <div class="{{AdminAgentsViewClass}}">
                <div class="admin-list">
                  <div class="{{RowClass}}" data-repeat="AdminAgents" data-admin-agent="{{Nick}}">
                    <div class="admin-agent-row">
                      <span class="admin-pfp">{{Initials}}</span>
                      <span class="admin-agent-main">
                        <span class="admin-nick">{{Nick}}</span>
                        <span class="admin-detail">{{Detail}}</span>
                        <span class="{{StateClass}}">{{State}}</span>
                      </span>
                    </div>
                  </div>
                </div>
                <div class="admin-side">
                  <div class="admin-section">Add an agent</div>
                  <div class="connect-label">Name</div>
                  <cupri-textfield class="admin-field" value="{{NewAgentNick}}" placeholder="scribe"></cupri-textfield>
                  <div class="connect-label">Rooms</div>
                  <cupri-textfield class="admin-field" value="{{NewAgentRooms}}" placeholder="#main, #notes"></cupri-textfield>
                  <div class="connect-label">Skills</div>
                  <cupri-textfield class="admin-field" value="{{NewAgentSkills}}" placeholder="notes, minutes"></cupri-textfield>
                  <div class="admin-toggles">
                    <cupri-button class="admin-locality">{{NewAgentLocality}}</cupri-button>
                    <cupri-button class="admin-clearance">{{NewAgentClearance}}</cupri-button>
                  </div>
                  <div class="admin-hint">A frontier agent runs on somebody else's model, so anything it is shown leaves.</div>
                  <cupri-button class="admin-add">Create and get a code</cupri-button>

                  <div class="admin-section">Selected: {{AdminSelected}}</div>
                  <cupri-button class="admin-reissue">New code for a new machine</cupri-button>
                  <cupri-button class="admin-remove">Remove this agent</cupri-button>
                  <div class="admin-hint">Removing takes effect at once — its key stops working on the next thing it tries.</div>
                </div>
              </div>
              <div class="{{AdminUsersViewClass}}">
                <div class="admin-list">
                  <div class="{{RowClass}}" data-repeat="AdminUsers" data-admin-user="{{Username}}">
                    <div class="admin-agent-row">
                      <span class="admin-pfp">{{Initials}}</span>
                      <span class="admin-agent-main">
                        <span class="admin-nick">{{Username}}</span>
                        <span class="admin-detail">{{Detail}}</span>
                      </span>
                    </div>
                  </div>
                </div>
                <div class="admin-side">
                  <div class="admin-section">Add a user</div>
                  <div class="connect-label">Name</div>
                  <cupri-textfield class="admin-field" value="{{NewUserName}}" placeholder="carol"></cupri-textfield>
                  <div class="admin-toggles">
                    <cupri-button class="admin-user-role">{{NewUserRole}}</cupri-button>
                  </div>
                  <div class="admin-hint">An admin manages users and agents here, and is added to every room an agent opens.</div>
                  <cupri-button class="admin-user-add">Create and get a password</cupri-button>

                  <div class="admin-section">Selected: {{AdminUserSelected}}</div>
                  <cupri-button class="admin-user-reset">New temporary password</cupri-button>
                  <cupri-button class="admin-user-toggle">{{AdminUserToggleLabel}}</cupri-button>
                  <cupri-button class="admin-user-remove">Remove this user</cupri-button>
                  <div class="admin-hint">Their password stops working at once. You cannot remove yourself.</div>
                </div>
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
        .msg-main { display: flex; flex-direction: column; flex: 1; min-width: 0; padding-left: 10px; }
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
        /* The padding centres the line, and the minimum is one line of it. Asking for a taller
           minimum instead puts all of the slack under the text, which reads as a field whose
           contents have slipped.

           min-width: 0 is what actually lets it flex. A flex item defaults to min-width: auto and
           will not shrink below its own content, and this component reports a wide one — so the
           field kept 288px it had no room for and shoved Send off the right edge of the window
           the moment a third button appeared beside it. Measured: with it, the row ends exactly
           where the composer does; without it, 92px past.

           The transparent border is the focus ring, held at its full width in every state. Since
           CupriFace 0.10.1 the component's hover and focus rules set border-color ALONE, so the
           width is the app's to declare and the component only recolours what it finds — which
           means `border: 0` here does not give a borderless field, it gives a field with no
           visible focus ring at all. Declaring the width up front is also what stops the bar
           twitching as the pointer crosses it: before 0.10.1 those rules redeclared the whole
           shorthand, and a field written with no border grew 4px on hover and lifted the
           composer, its buttons and the hint with it (CupriFace#93). The padding is 2px lighter
           than it would otherwise be, to pay for the border. */
        .composer { flex: 1; min-width: 0; min-height: 18px; max-height: 110px; background: #11151b;
                    color: #f3f5f7; border: 2px solid transparent; padding: 3px 0;
                    caret-color: #fb7185; }
        /* The component's own focus ring is amber, which on this palette reads as a warning
           rather than as "you are typing here". Colour only — the width is already reserved. */
        .composer:focus { border-color: #fb7185; }
        .composer-hint { font-size: 10px; color: #5f6877; margin: 7px 0 0 0; }

        /* Above the composer, not below it: a list that drops downwards would fall off the
           bottom of the window, which is exactly where the composer already is. */
        .mentions { display: flex; flex-direction: column; margin-bottom: 6px; padding: 5px;
                    background: #151920; border: 1px solid #303744; border-radius: 12px;
                    box-shadow: 0 18px 55px #00000052; }
        .mentions.hidden { display: none; }
        .mention { display: flex; flex-direction: row; align-items: center; padding: 5px 7px;
                   border-radius: 8px; cursor: pointer; }
        .mention:hover { background: #1b2029; }
        /* The selected row is what Enter takes, so it is marked as plainly as the active room —
           and the name in the accent, because at a glance the colour is read before the fill. */
        .mention.selected { background: #262d3a; }
        .mention.selected .mention-nick { color: #fb7185; font-weight: bold; }
        .mention-pfp { width: 24px; height: 24px; border-radius: 8px; display: flex;
                       align-items: center; justify-content: center; font-size: 9px;
                       font-weight: bold; background: #262d38; border: 1px solid #384150;
                       color: #cbd5e1; }
        .mention-nick { flex: 1; min-width: 0; font-size: 13px; padding-left: 9px; }
        .mention-meta { font-size: 10px; color: #8d97a6; }
        /* Said here rather than in the composer hint, because it is only true while this is up —
           and Ctrl on an arrow is not a guess anyone makes unprompted. */
        .mention-hint { font-size: 9px; color: #5f6877; padding: 4px 7px 1px 7px; }

        /* Buttons are sized by padding, never by height. An explicit height leaves the label
           against the top of the box — line-height does not move it — and the border sits outside
           that height, so a bordered button and a borderless one declared the same height come out
           2px apart and sit on different lines. Every button here carries a border for that
           reason, the accent ones in their own colour.

           text-align centres the word. A min-width stretches the label's own box to the button's
           full inner width, and text in a box is left-aligned, so every button wider than its
           word wore the word against its left padding. */
        .mic { margin-left: 8px; min-width: 64px; padding: 6px 14px; font-size: 13px; background: #1b2029; color: #f3f5f7;
               border: 1px solid #333a46; border-radius: 10px; text-align: center; }
        /* The gate's state, said in colour as well as in words: a room microphone is watched
           from across a desk, where the label is too small to read. */
        .mic.armed { background: #2b3a4a; }
        .mic.hearing { background: #2f6b3f; }
        .mic.working { background: #6b5a2f; }
        .mic.hidden { display: none; }
        .attach-open { margin-left: 8px; min-width: 66px; padding: 6px 14px; font-size: 13px; background: #1b2029; color: #f3f5f7;
                       border: 1px solid #333a46; border-radius: 10px; text-align: center; }
        .attach-open.hidden { display: none; }
        .send { min-width: 62px; padding: 6px 14px; font-size: 13px; margin-left: 8px; background: #ef4444; color: #ffffff;
                border: 1px solid #ef4444; border-radius: 10px; font-weight: bold; text-align: center; }

        .voice-row { display: flex; flex-direction: row; align-items: center; padding: 0 18px 10px 18px; }
        .voice-row.hidden { display: none; }
        .voice-status { flex: 1; color: #8d97a6; font-size: 12px; }
        .readback { padding: 5px 12px; background: #1b2029; color: #8d97a6; border: 1px solid #333a46;
                    border-radius: 10px; font-size: 12px; text-align: center; }
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
        /* The inset is a margin on the card rather than padding on the overlay. The overlay is
           `width: 100%`, and padding adds to a width unless the box is told otherwise, so padding
           there would have pushed 112px of the panel off the right edge. */
        .toolpanel-inner { display: flex; flex-direction: column; flex: 1; margin: 36px 56px;
                           background: #151920; border-radius: 14px; padding: 16px; }
        .toolpanel-head { display: flex; flex-direction: row; align-items: center; padding-bottom: 12px; }
        .toolpanel-title { flex: 1; font-weight: bold; }
        .toolpanel-status { color: #8d97a6; font-size: 12px; padding-right: 12px; }
        .tools-save { min-width: 72px; padding: 6px 14px; font-size: 13px; margin-right: 8px; background: #ef4444; color: #ffffff;
                      border: 1px solid #ef4444; border-radius: 10px; text-align: center; }
        .tools-close { min-width: 72px; padding: 6px 14px; font-size: 13px; background: #1b2029; color: #f3f5f7;
                       border: 1px solid #333a46; border-radius: 10px; text-align: center; }
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
        .tool-line { display: flex; flex-direction: row; align-items: center; }
        .tool-mark { width: 24px; color: #34d399; font-size: 11px; }
        .tool-name { flex: 1; font-size: 13px; }
        .tool-server { color: #8d97a6; font-size: 11px; }
        .tool-desc { color: #8d97a6; font-size: 11px; padding-left: 24px; }


        /* The agents page. Same overlay shape as the tool panel, for the same reason: managing who
           may speak in a room is a deliberate, occasional act that wants the width. */
        .admin-open { width: 42px; height: 42px; border-radius: 14px; display: flex;
                      align-items: center; justify-content: center; font-size: 12px;
                      font-weight: bold; background: #1b2029; color: #bec5cf; cursor: pointer;
                      margin-top: 10px; }
        .admin-open:hover { background: #242a34; color: #ffffff; }
        .admin-open.hidden { display: none; }

        .adminpanel { position: absolute; left: 0; top: 0; width: 100%; height: 100%;
                      display: flex; background: #0b0d10; }
        .adminpanel.hidden { display: none; }
        .adminpanel-inner { display: flex; flex-direction: column; flex: 1; margin: 36px 56px;
                            background: #151920; border-radius: 14px; padding: 16px; }
        .adminpanel-body { display: flex; flex-direction: row; flex: 1; }
        .adminpanel-body.hidden { display: none; }

        .admin-tab { font-size: 15px; font-weight: bold; color: #5b6472; margin-right: 14px;
                     cursor: pointer; }
        .admin-tab:hover { color: #cfd6e2; }
        .admin-tab.selected { color: #ffffff; }

        .admin-list { flex: 1; min-width: 0; padding-right: 14px; overflow: scroll; }
        .admin-agent { padding: 8px; border-radius: 9px; margin-bottom: 6px; background: #111419;
                       cursor: pointer; }
        .admin-agent:hover { background: #171b22; }
        .admin-agent.selected { background: #202530; }
        .admin-agent-row { display: flex; flex-direction: row; align-items: center; }
        .admin-pfp { width: 30px; height: 30px; border-radius: 10px; display: flex;
                     align-items: center; justify-content: center; font-size: 10px;
                     font-weight: bold; background: #262d38; border: 1px solid #384150;
                     color: #cbd5e1; }
        .admin-agent-main { display: flex; flex-direction: column; flex: 1; min-width: 0;
                            padding-left: 10px; }
        .admin-nick { font-size: 13px; font-weight: bold; }
        .admin-detail { font-size: 10px; color: #8d97a6; margin-top: 1px; }
        /* The fingerprint when there is one; what is missing when there is not. An identity with no
           key cannot connect, and that is the thing an operator most needs to see. */
        .admin-state { font-size: 10px; color: #34d399; margin-top: 2px; }
        .admin-state.pending { color: #fbbf24; }

        .admin-side { width: 300px; display: flex; flex-direction: column; }
        .admin-section { font-size: 10px; font-weight: bold; color: #717c8d; padding: 10px 0 2px 0; }
        .admin-field { background: #0d1014; color: #f3f5f7; border: 1px solid #252b35;
                       border-radius: 10px; padding: 7px 10px; }
        .admin-toggles { display: flex; flex-direction: row; margin-top: 10px; }
        .admin-locality, .admin-clearance, .admin-user-role { flex: 1; min-width: 0; padding: 6px 10px; font-size: 12px;
                                            background: #1b2029; color: #f3f5f7;
                                            border: 1px solid #333a46; border-radius: 10px;
                                            text-align: center; margin-right: 8px; }
        .admin-hint { font-size: 10px; color: #5f6877; margin: 8px 0; }
        .admin-add, .admin-user-add { margin-top: 4px; padding: 7px 14px; font-size: 13px; background: #ef4444;
                     color: #ffffff; border: 1px solid #ef4444; border-radius: 10px;
                     font-weight: bold; text-align: center; }
        .admin-reissue, .admin-user-reset, .admin-user-toggle { margin-top: 4px; padding: 6px 14px; font-size: 12px; background: #1b2029;
                         color: #f3f5f7; border: 1px solid #333a46; border-radius: 10px;
                         text-align: center; }
        .admin-remove, .admin-user-remove { margin-top: 6px; padding: 6px 14px; font-size: 12px; background: #2a1618;
                        color: #fca5a5; border: 1px solid #4c1d1d; border-radius: 10px;
                        text-align: center; }
        .admin-close { min-width: 72px; padding: 6px 14px; font-size: 13px; background: #1b2029;
                       color: #f3f5f7; border: 1px solid #333a46; border-radius: 10px;
                       text-align: center; }

        /* The code is the one secret on this screen and it is shown exactly once, so it is given
           the width of the page rather than tucked into the form that produced it. */
        .admin-code { display: flex; flex-direction: column; background: #16301c;
                      border: 1px solid #2f6b3f; border-radius: 12px; padding: 10px 12px;
                      margin-bottom: 12px; }
        .admin-code.hidden { display: none; }
        .admin-code-note { font-size: 11px; color: #a7e3bb; }
        .admin-code-row { display: flex; flex-direction: row; align-items: center; margin-top: 6px; }
        .admin-code-value { flex: 1; min-width: 0; font-size: 13px; color: #ffffff; }
        .admin-copy { min-width: 64px; padding: 5px 12px; font-size: 12px; background: #1b2029;
                      color: #f3f5f7; border: 1px solid #333a46; border-radius: 9px;
                      text-align: center; }

        /* Over everything, and its own colour: until this is dealt with there is no room to
           look at behind it. */
        /* No padding here; the offset is on the card instead, for the reason the tool panel has
           the same shape — this is `width: 100%`, and padding adds to a width unless the box is
           told otherwise. Padding here once put the card's centre 60px right of the viewport's. */
        .connect { position: absolute; left: 0; top: 0; width: 100%; height: 100%;
                   display: flex; justify-content: center; background: #0b0d10; }
        .connect.hidden { display: none; }
        /* Centred by the container rather than by auto margins on the card — one lone child is
           what justify-content is for. Top-aligned on purpose: a short viewport must not push the
           card off-screen. */
        .connect-card { width: 360px; margin-top: 60px;
                        display: flex; flex-direction: column; background: #151920;
                        border-radius: 14px; padding: 20px; height: 252px;
                        box-shadow: 0 18px 55px #00000052; }
        .connect-title { font-weight: bold; font-size: 20px; padding-bottom: 12px; }
        .connect-label { font-size: 11px; color: #8d97a6; padding-bottom: 4px; padding-top: 8px; }
        .connect-field { background: #0d1014; color: #f3f5f7; border: 1px solid #252b35;
                         border-radius: 10px; padding: 7px 10px; }
        .connect-go { margin-top: 16px; padding: 9px 0; font-size: 13px; text-align: center;
                      background: #ef4444; color: #ffffff; border: 1px solid #ef4444;
                      border-radius: 10px; font-weight: bold; }
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

        // Enter sends and Shift+Enter writes a newline, which is what people expect of a chat
        // composer and what they type without being told.
        //
        // Per-field rather than a global Enter shortcut: a global one would eat the newline in
        // every other multi-line field on the page. The engine commits the edit buffer before
        // calling this, so Send reads the character just typed, and keeps focus afterwards — a
        // composer goes on composing.
        doc.OnSubmit("data-composer", _ =>
        {
            // Enter takes the highlighted suggestion when the list is up, and only sends when it
            // is not. This lives inside the submit handler rather than in a shortcut of its own
            // because a bare "Enter" binding wins against submit-on-enter outright — measured —
            // which would leave the composer unable to send at all.
            if (ViewModel.TakeMention(out var rubOut, out var typeIn))
            {
                for (var i = 0; i < rubOut; i++)
                {
                    doc.DispatchKey("", EditKey.Backspace);
                }

                foreach (var ch in typeIn)
                {
                    doc.DispatchKey(ch.ToString(), EditKey.None);
                }

                doc.Refresh();
                return true;
            }

            Send();
            return true;
        });

        // Ctrl, not a bare arrow, and not by choice. A focused field swallows plain Up and Down
        // before any shortcut sees them — measured: with the composer focused the keystroke comes
        // back handled and the binding never fires — while a Ctrl chord is delivered wherever
        // focus is. Raised upstream; when a bare arrow can reach an open list this loses the Ctrl.
        //
        // Nothing is taken away from editing a draft either way: the composer does not move its
        // caret vertically at all today, so plain Up and Down are inert in it.
        doc.OnShortcut(KeyMods.Ctrl, "Down", () => MoveMention(1));
        doc.OnShortcut(KeyMods.Ctrl, "Up", () => MoveMention(-1));

        // Clicking one takes it by rewriting the composer, which the keyboard path deliberately
        // cannot do: a click has already moved focus off the field, so there is no buffer of its
        // own left to contradict the assignment.
        doc.OnAction("data-mention", e =>
        {
            if (string.IsNullOrEmpty(e.Value) || !ViewModel.AcceptMention(e.Value))
            {
                return false;
            }

            doc.Refresh();
            return true;
        });

        // Escape abandons an edit. It fires below the engine's own dismissals, so an open context
        // menu closes first and only a spare Escape reaches this — and it arrives before the
        // field is blurred, so it still means "cancel" while the field being cancelled has focus.
        doc.OnShortcut(KeyMods.None, "Escape", () =>
        {
            // The list first: Escape means "put away the thing that just appeared", and losing a
            // half-written edit because a suggestion happened to be showing would be maddening.
            if (ViewModel.MentionsOpen)
            {
                ViewModel.CloseMentions();
                doc.Refresh();
                return;
            }

            CancelEdit();
        });

        // ---- The agents page ----

        doc.OnAction("data-admin-open", _unused =>
        {
            ViewModel.ShowAdminPanel(true);
            _ = AgentsListAsync();
            doc.Refresh();
            return true;
        });

        doc.OnClick(".admin-close", _unused =>
        {
            ViewModel.ShowAdminPanel(false);
            doc.Refresh();
        });

        doc.OnAction("data-admin-agent", e =>
        {
            if (string.IsNullOrEmpty(e.Value))
            {
                return false;
            }

            ViewModel.SelectAdminAgent(e.Value);
            doc.Refresh();
            return true;
        });

        // Two values and three values respectively, so a tap that cycles beats a control the engine
        // has no dropdown for.
        doc.OnClick(".admin-locality", _unused =>
        {
            ViewModel.CycleNewAgentLocality();
            doc.Refresh();
        });

        doc.OnClick(".admin-clearance", _unused =>
        {
            ViewModel.CycleNewAgentClearance();
            doc.Refresh();
        });

        doc.OnClick(".admin-add", _unused =>
        {
            if (ViewModel.ReadNewAgent() is not { } form)
            {
                doc.Refresh();
                return;
            }

            _ = AgentCreateAsync(form.Nick, form.Rooms, form.Skills, form.Locality, form.Clearance);
        });

        doc.OnClick(".admin-reissue", _unused =>
        {
            if (ViewModel.Model.AdminSelected.Length > 0)
            {
                _ = AgentReissueAsync(ViewModel.Model.AdminSelected);
            }
        });

        doc.OnClick(".admin-remove", _unused =>
        {
            if (ViewModel.Model.AdminSelected.Length > 0)
            {
                _ = AgentRemoveAsync(ViewModel.Model.AdminSelected);
            }
        });

        // ---- The users tab ----

        doc.OnAction("data-admin-tab", e =>
        {
            var users = e.Value == "users";
            ViewModel.ShowAdminTab(users);
            _ = users ? UsersListAsync() : AgentsListAsync();
            doc.Refresh();
            return true;
        });

        doc.OnAction("data-admin-user", e =>
        {
            if (string.IsNullOrEmpty(e.Value))
            {
                return false;
            }

            ViewModel.SelectAdminUser(e.Value);
            doc.Refresh();
            return true;
        });

        doc.OnClick(".admin-user-role", _unused =>
        {
            ViewModel.CycleNewUserRole();
            doc.Refresh();
        });

        doc.OnClick(".admin-user-add", _unused =>
        {
            if (ViewModel.ReadNewUser() is not { } form)
            {
                doc.Refresh();
                return;
            }

            _ = UserCreateAsync(form.Username, form.IsAdmin);
        });

        doc.OnClick(".admin-user-reset", _unused =>
        {
            if (ViewModel.Model.AdminUserSelected.Length > 0)
            {
                _ = UserResetAsync(ViewModel.Model.AdminUserSelected);
            }
        });

        // The toggle asks for the OPPOSITE of what the row currently is: its label already reads
        // as the action, so this is the click doing what the button said it would.
        doc.OnClick(".admin-user-toggle", _unused =>
        {
            if (ViewModel.SelectedUser is { } row)
            {
                _ = UserSetAdminAsync(row.Username, !row.IsAdmin);
            }
        });

        doc.OnClick(".admin-user-remove", _unused =>
        {
            if (ViewModel.Model.AdminUserSelected.Length > 0)
            {
                _ = UserRemoveAsync(ViewModel.Model.AdminUserSelected);
            }
        });

        // The code exists in readable form for as long as this page is open and no longer, so
        // copying it is the whole point of showing it.
        doc.OnClick(".admin-copy", _unused =>
        {
            if (ViewModel.Model.AdminCode.Length > 0)
            {
                Clipboard.SetText(ViewModel.Model.AdminCode);
            }
        });

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

        // Typing is written into the model by the engine rather than queued as a mutation, so the
        // suggestion list is recomputed here. It compares one string on a frame where nothing was
        // typed, which is nearly all of them.
        if (ViewModel.RefreshMentions())
        {
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

    /// <summary>Walks the suggestion list, if it is up. A no-op otherwise, so the arrows stay
    /// harmless when nobody is naming anyone.</summary>
    private void MoveMention(int delta)
    {
        if (!ViewModel.MentionsOpen)
        {
            return;
        }

        ViewModel.MoveMentionSelection(delta);
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
