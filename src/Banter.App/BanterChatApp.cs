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

    /// <summary>Creating an agent; the reply's one-time code is the host's to show.</summary>
    public Func<AgentForm, Task> AgentCreateAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Saving an existing agent. Same form as the create, because a create is a save of
    /// something that did not exist yet.</summary>
    public Func<AgentForm, Task> AgentSaveAsync { get; init; } = _ => Task.CompletedTask;

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

    /// <summary>Told when the interface scale changes, so the head can remember it.</summary>
    public Action<float> ZoomChanged { get; init; } = _ => { };

    /// <summary>The scale to start at, as the head last saved it.</summary>
    public float InitialZoom { get; init; } = 1f;

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

    public override string Html => $$$"""
        <div class="app">
          <div class="rail">
            <div class="logo">
              <div class="icon-chat">
                <div class="chat-body"></div>
                <div class="chat-tail"></div>
              </div>
            </div>
            <div class="{{ToolsButtonClass}}" data-tools-open="1">
              <div class="icon-tools">
                <div class="tools-track tools-track-top"></div>
                <div class="tools-track tools-track-bottom"></div>
                <div class="tools-knob tools-knob-top"></div>
                <div class="tools-knob tools-knob-bottom"></div>
              </div>
            </div>
            <div class="{{AgentsButtonClass}}" data-agents-open="1">
              <div class="icon-agents">
                <div class="net-edge net-edge-up"></div>
                <div class="net-edge net-edge-left"></div>
                <div class="net-edge net-edge-right"></div>
                <div class="net-node net-hub"></div>
                <div class="net-node net-top"></div>
                <div class="net-node net-bottom-left"></div>
                <div class="net-node net-bottom-right"></div>
              </div>
            </div>
            <div class="{{UsersButtonClass}}" data-users-open="1">
              <div class="icon-users">
                <div class="user-head"></div>
                <div class="user-body"></div>
              </div>
            </div>
            <div class="{{SettingsButtonClass}}" data-settings-open="1">
              <div class="icon-settings">
                <div class="cog-tooth cog-a"></div>
                <div class="cog-tooth cog-b"></div>
                <div class="cog-tooth cog-c"></div>
                <div class="cog-ring"></div>
                <div class="cog-hole"></div>
              </div>
            </div>
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
            <div class="{{RosterAgentsTitleClass}}">Agents</div>
            <div class="{{RowClass}}" data-repeat="Agents">
              <div class="agent-row">
                <span class="agent-pfp">{{Initials}}</span>
                <span class="agent-main">
                  <span class="agent-line"><span class="agent-nick">{{Nick}}</span><span class="agent-role">{{Role}}</span></span>
                  <span class="agent-meta">{{Locality}} · {{Skills}}</span>
                </span>
              </div>
            </div>
            <div class="{{RosterUsersTitleClass}}">Users</div>
            <div class="{{RowClass}}" data-repeat="Users">
              <div class="agent-row">
                <span class="member-pfp">{{Initials}}</span>
                <span class="agent-main">
                  <span class="agent-line"><span class="agent-nick">{{Nick}}</span><span class="member-badge">{{Badge}}</span></span>
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
          {{{AgentsPage}}}
          {{{UsersPage}}}
          <div class="{{SettingsPanelClass}} settings-overlay">
            <div class="mgmt-backdrop" data-settings-close="1"></div>
            <div class="mgmt-card settings-card">
              <div class="mgmt-pane">
                <div class="mgmt-detail-head">
                  <div class="mgmt-list-heading">
                    <div class="mgmt-title">Settings</div>
                    <div class="mgmt-subtitle">How Banter looks on this machine.</div>
                  </div>
                  <cupri-button class="mgmt-remove settings-close">Close</cupri-button>
                </div>
                <div class="mgmt-fields">
                  <div class="mgmt-field">
                    <div class="mgmt-label">Zoom</div>
                    <div class="mgmt-control">
                      <div class="mgmt-choices wrap">
                        <div class="{{RowClass}}" data-repeat="ZoomChoices" data-zoom="{{Value}}">
                          <div class="{{DotClass}}"></div>
                          <div class="mgmt-choice-text">
                            <div class="mgmt-choice-label">{{Label}}</div>
                          </div>
                        </div>
                      </div>
                      <div class="mgmt-hint">On top of the scale this screen already gets: 100% is what the window size deserves, not one pixel per pixel. Remembered, and Ctrl with the scroll wheel does the same thing.</div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
          <!-- Over every page, including the ones that raise it: a question with the screen it
               came from still visible behind it. -->
          <div class="{{ConfirmClass}}">
            <div class="confirm-backdrop" data-confirm-no="1"></div>
            <div class="confirm-card">
              <div class="confirm-title">{{ConfirmTitle}}</div>
              <div class="confirm-body">{{ConfirmBody}}</div>
              <div class="confirm-buttons">
                <cupri-button class="confirm-cancel">Cancel</cupri-button>
                <cupri-button class="confirm-go">{{ConfirmAction}}</cupri-button>
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


    /// <summary>
    /// One management page: a list with a "new" button on the left, a form for whatever is
    /// selected on the right, one footer.
    ///
    /// <para>Agents and users are the same page with different bindings, so this is a template
    /// called twice rather than two blocks of markup kept in step by hand. The two had already
    /// drifted once — the users half grew a role toggle the agents half never got — and markup
    /// that must match is markup that will stop matching.</para>
    ///
    /// <para>Bindings arrive already wrapped in their braces (<c>"{{Foo}}"</c>) so this method
    /// never has to spell a brace it does not mean.</para>
    /// </summary>
    private static string ManagementPage(
        string panelClass,
        string closeAction,
        string listTitle,
        string listSubtitle,
        string newLabel,
        string newAction,
        string status,
        string rowsBinding,
        string rowAction,
        string rowKey,
        string rowBody,
        string emptyClass,
        string emptyText,
        string detailClass,
        string detailTitle,
        string detailSubtitle,
        string removeClass,
        string removeLabel,
        string fields,
        string dirtyClass,
        string cancelClass,
        string saveClass,
        string saveLabel) => $$$"""
              <div class="{{{panelClass}}}">
                <!-- The backdrop is a sibling BEFORE the card, so the card paints over it and
                     takes the hit. A click that reaches the backdrop is therefore a click that
                     missed the card, which is exactly what "outside" means. -->
                <div class="mgmt-backdrop" {{{closeAction}}}="1"></div>
                <div class="mgmt-card">
                  <div class="mgmt-list">
                    <div class="mgmt-list-head">
                      <div class="mgmt-list-heading">
                        <div class="mgmt-title">{{{listTitle}}}</div>
                        <div class="mgmt-subtitle">{{{listSubtitle}}}</div>
                      </div>
                      <cupri-button class="mgmt-new" {{{newAction}}}="1">+ {{{newLabel}}}</cupri-button>
                    </div>
                    <div class="mgmt-status">{{{status}}}</div>
                    <div class="mgmt-rows">
                      <div class="{{RowClass}}" data-repeat="{{{rowsBinding}}}" {{{rowAction}}}="{{{rowKey}}}">
              {{{rowBody}}}
                      </div>
                    </div>
                  </div>
                  <div class="mgmt-pane">
                    <div class="{{{emptyClass}}}">{{{emptyText}}}</div>
                    <div class="{{{detailClass}}}">
                      <div class="mgmt-detail-head">
                        <div class="mgmt-list-heading">
                          <div class="mgmt-title">{{{detailTitle}}}</div>
                          <div class="mgmt-subtitle">{{{detailSubtitle}}}</div>
                        </div>
                        <cupri-button class="{{{removeClass}}}">{{{removeLabel}}}</cupri-button>
                      </div>
                      <div class="{{AdminCodeClass}}">
                        <div class="mgmt-secret-note">{{AdminCodeFor}}</div>
                        <div class="mgmt-secret-row">
                          <span class="mgmt-secret-value">{{AdminCode}}</span>
                          <cupri-button class="mgmt-copy">Copy</cupri-button>
                        </div>
                      </div>
                      <div class="mgmt-fields">
              {{{fields}}}
                      </div>
                      <div class="mgmt-footer">
                        <div class="{{{dirtyClass}}}">You have unsaved changes</div>
                        <cupri-button class="{{{cancelClass}}}">Cancel</cupri-button>
                        <cupri-button class="{{{saveClass}}}">{{{saveLabel}}}</cupri-button>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
              """;

    /// <summary>A labelled row: what it is on the left, the control and its explanation right.</summary>
    private static string Field(string label, string control, string hint, string fieldClass = "mgmt-field") => $$$"""
    <div class="{{{fieldClass}}}">
      <div class="mgmt-label">{{{label}}}</div>
      <div class="mgmt-control">
    {{{control}}}
        <div class="mgmt-hint">{{{hint}}}</div>
      </div>
    </div>
    """;

    private static string TextControl(string binding, string placeholder) =>
        $$$"""<cupri-textfield class="mgmt-input" value="{{{binding}}}" placeholder="{{{placeholder}}}"></cupri-textfield>""";

    /// <summary>Read-only values are painted, not put in a field: a box you can click into and
    /// not change is a promise the screen cannot keep.</summary>
    private static string ReadOnlyControl(string binding) =>
        $$$"""<div class="mgmt-readonly">{{{binding}}}</div>""";

    /// <summary>
    /// A name field: editable while creating, painted while editing. Both are emitted and the
    /// field's own class decides which shows — the alternative is a box you can type into that
    /// silently refuses to rename anything.
    /// </summary>
    private static string NameControl(string editable, string fixedValue, string placeholder) =>
        TextControl(editable, placeholder) + Environment.NewLine +
        $$$"""<div class="mgmt-fixed">{{{fixedValue}}}</div>""";

    /// <summary>A radio-card group. Cards rather than a dropdown because the choice needs its
    /// consequence next to it — "frontier" is a word, "anything it is shown leaves" is a decision.</summary>
    private static string ChoiceControl(string binding, string action) => $$$"""
    <div class="mgmt-choices">
      <div class="{{RowClass}}" data-repeat="{{{binding}}}" {{{action}}}="{{Value}}">
        <div class="{{DotClass}}"></div>
        <div class="mgmt-choice-text">
          <div class="mgmt-choice-label">{{Label}}</div>
          <div class="mgmt-choice-hint">{{Hint}}</div>
        </div>
      </div>
    </div>
    """;

    private static string InlineAction(string cls, string label) =>
        $$$"""<cupri-button class="{{{cls}}}">{{{label}}}</cupri-button>""";

    private static readonly string AgentRowBody = """
    <div class="mgmt-row-inner">
      <span class="mgmt-pfp">{{Initials}}</span>
      <span class="mgmt-row-main">
        <span class="mgmt-row-name">{{Nick}}</span>
        <span class="mgmt-row-detail">{{Detail}}</span>
        <span class="{{StateClass}}">{{State}}</span>
      </span>
    </div>
    """;

    private static readonly string UserRowBody = """
    <div class="mgmt-row-inner">
      <span class="mgmt-pfp">{{Initials}}</span>
      <span class="mgmt-row-main">
        <span class="mgmt-row-name">{{Username}}</span>
        <span class="mgmt-row-detail">{{Detail}}</span>
      </span>
    </div>
    """;

    private static string AgentsPage => ManagementPage(
        panelClass: "{{AgentsPanelClass}}",
        closeAction: "data-agents-close",
        listTitle: "Agents",
        listSubtitle: "Who may run in your rooms.",
        newLabel: "New agent",
        newAction: "data-agent-new",
        status: "{{AgentsStatus}}",
        rowsBinding: "AdminAgents",
        rowAction: "data-admin-agent",
        rowKey: "{{Nick}}",
        rowBody: AgentRowBody,
        emptyClass: "{{AgentEmptyClass}}",
        emptyText: "Choose an agent to edit it, or create a new one.",
        detailClass: "{{AgentDetailClass}}",
        detailTitle: "{{AgentDetailTitle}}",
        detailSubtitle: "{{AgentDetailSubtitle}}",
        removeClass: "{{AgentRemoveClass}}",
        removeLabel: "Remove agent",
        fields: string.Concat(
            Field("Name", NameControl("{{AgentFormNick}}", "{{AgentFormNickReadonly}}", "scribe"),
                "What it answers to in a room. Nothing else may share it, and it cannot be changed later.",
                "{{AgentNickFieldClass}}"),
            Field("Rooms", TextControl("{{AgentFormRooms}}", "#main, #notes"),
                "Comma separated."),
            Field("Skills", TextControl("{{AgentFormSkills}}", "notes, minutes"),
                "What the delegator matches on when handing out work."),
            Field("Runs on", ChoiceControl("AgentLocalityChoices", "data-agent-locality"),
                "The axis that decides whether anything said in the room may leave it."),
            Field("Clearance", ChoiceControl("AgentClearanceChoices", "data-agent-clearance"),
                "The most sensitive material this agent may be shown."),
            Field("Delegation", ChoiceControl("AgentDelegatorChoices", "data-agent-delegator"),
                "A pinned agent wins the election outright, so this is an operator's call."),
            Field("Cost", TextControl("{{AgentFormCost}}", "agent decides"),
                "Lower is cheaper, and only ever a tie-break. Empty lets the agent say."),
            Field("Key", ReadOnlyControl("{{AgentFingerprint}}") + "\n" +
                InlineAction("{{AgentReissueClass}}", "New code for a new machine"),
                "Which machine holds this identity. Reissuing retires the old one.",
                "{{AgentKeyFieldClass}}")),
        dirtyClass: "{{AgentDirtyClass}}",
        cancelClass: "mgmt-cancel-agent",
        saveClass: "mgmt-save-agent",
        saveLabel: "{{AgentSaveLabel}}");

    private static string UsersPage => ManagementPage(
        panelClass: "{{UsersPanelClass}}",
        closeAction: "data-users-close",
        listTitle: "Users",
        listSubtitle: "Who may sign in.",
        newLabel: "New user",
        newAction: "data-user-new",
        status: "{{UsersStatus}}",
        rowsBinding: "AdminUsers",
        rowAction: "data-admin-user",
        rowKey: "{{Username}}",
        rowBody: UserRowBody,
        emptyClass: "{{UserEmptyClass}}",
        emptyText: "Choose a user to edit them, or create a new one.",
        detailClass: "{{UserDetailClass}}",
        detailTitle: "{{UserDetailTitle}}",
        detailSubtitle: "{{UserDetailSubtitle}}",
        removeClass: "{{UserRemoveClass}}",
        removeLabel: "Remove user",
        fields: string.Concat(
            Field("Name", NameControl("{{UserFormName}}", "{{UserFormNameReadonly}}", "carol"),
                "They sign in with this. It cannot be changed later.", "{{UserNickFieldClass}}"),
            Field("Role", ChoiceControl("UserRoleChoices", "data-user-role"),
                "An admin manages this page, and is added to every room an agent opens."),
            Field("Password", InlineAction("{{UserResetClass}}", "New temporary password"),
                "Nobody can read the current one, including you. A reset replaces it.")),
        dirtyClass: "{{UserDirtyClass}}",
        cancelClass: "mgmt-cancel-user",
        saveClass: "mgmt-save-user",
        saveLabel: "{{UserSaveLabel}}");

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
                align-items: center; justify-content: center;
                background: linear-gradient(145deg, #ef4444, #991b1b);
                box-shadow: 0 8px 24px #ef444433; }

        /* ── Rail icons ───────────────────────────────────────────────────────────────────────
           Drawn from boxes rather than set as glyphs. A glyph would come from whatever font the
           host machine happens to resolve — Skia falls back to system fonts, and no face is
           embedded — so the same rail would render differently on desktop, Android and the web,
           or show tofu. Boxes go through the same layout and paint on every head.

           The engine gives us circles (single-value border-radius; the four-value shorthand is
           NOT parsed, so every corner here is uniform), absolute positioning, and rotation about
           the centre. That is enough for all four of these. */

        .icon-chat { position: relative; width: 24px; height: 22px; }
        /* Body first, tail second: the tail is a rotated square tucked under the body's lower
           edge, and the body's own fill is what hides the half of it that would stick up. */
        .chat-body { position: absolute; left: 0px; top: 1px; width: 24px; height: 16px;
                     border-radius: 7px; background: #ffffff; }
        .chat-tail { position: absolute; left: 5px; top: 12px; width: 8px; height: 8px;
                     border-radius: 2px; background: #ffffff; transform: rotate(45deg); }

        .icon-users { position: relative; width: 22px; height: 22px; }
        .user-head { position: absolute; left: 7px; top: 2px; width: 10px; height: 10px;
                     border-radius: 5px; background: #bec5cf; }
        /* Shoulders: a wide bar with a radius half its height, so its top edge is a dome. */
        .user-body { position: absolute; left: 2px; top: 14px; width: 20px; height: 9px;
                     border-radius: 5px; background: #bec5cf; }

        /* A node graph: a hub wired to three satellites. Edges are drawn before nodes so the
           discs cover where the bars run under them. */
        .icon-agents { position: relative; width: 24px; height: 22px; }
        .net-node { position: absolute; border-radius: 4px; background: #bec5cf; }
        .net-hub { left: 9px; top: 8px; width: 7px; height: 7px; }
        .net-top { left: 9px; top: 0px; width: 7px; height: 7px; }
        .net-bottom-left { left: 1px; top: 15px; width: 7px; height: 7px; }
        .net-bottom-right { left: 17px; top: 15px; width: 7px; height: 7px; }
        /* Each edge is a bar centred on the midpoint between two node centres, its length the
           distance between them and its angle atan(dy/dx). Hub (12.5, 11.5) to the lower-left
           node (4.5, 18.5) runs down-LEFT, which is rotate(-41deg) — rotating a bar the other
           way draws the opposite diagonal and the icon becomes a cross. */
        .net-edge { position: absolute; background: #bec5cf; }
        .net-edge-up { left: 11px; top: 4px; width: 2px; height: 7px; }
        .net-edge-left { left: 3px; top: 14px; width: 11px; height: 2px; transform: rotate(-41deg); }
        .net-edge-right { left: 11px; top: 14px; width: 11px; height: 2px; transform: rotate(41deg); }

        /* A cog: a ring with a hole, and three teeth spaced by rotation. Three rather than the
           usual six or eight because a tooth is 4px wide here and any more becomes a smudge. */
        .icon-settings { position: relative; width: 22px; height: 22px; }
        .cog-ring { position: absolute; left: 4px; top: 4px; width: 14px; height: 14px;
                    border-radius: 7px; background: #bec5cf; }
        .cog-hole { position: absolute; left: 8px; top: 8px; width: 6px; height: 6px;
                    border-radius: 3px; background: #1b2029; }
        .cog-tooth { position: absolute; left: 9px; top: 0px; width: 4px; height: 22px;
                     border-radius: 2px; background: #bec5cf; }
        .cog-a { transform: rotate(0deg); }
        .cog-b { transform: rotate(60deg); }
        .cog-c { transform: rotate(120deg); }
        .rail-button:hover .cog-hole { background: #242a34; }

        /* Two tracks with a knob apiece. The panel behind this button is per-agent tool GRANTS —
           a row of switches — so sliders say what it does; a spanner drawn from boxes came out
           as a lollipop, and the shape has to survive being 22 pixels wide. */
        .icon-tools { position: relative; width: 22px; height: 22px; }
        .tools-track { position: absolute; left: 1px; width: 20px; height: 2px;
                       border-radius: 1px; background: #737d8c; }
        .tools-track-top { top: 5px; }
        .tools-track-bottom { top: 15px; }
        .tools-knob { position: absolute; width: 8px; height: 8px; border-radius: 4px;
                      background: #bec5cf; }
        .tools-knob-top { left: 12px; top: 2px; }
        .tools-knob-bottom { left: 2px; top: 12px; }
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
        .roster-title.hidden { display: none; }

        /* Humans share the agents' row geometry so the roster reads as one list with a divider,
           but their avatar is the timeline's squircle where an agent's is a circle — the section
           label does the telling, the shape merely agrees with it. */
        .member { padding: 7px 8px; border-radius: 9px; margin-bottom: 4px; }
        .member:hover { background: #171b22; }
        .member-pfp { width: 30px; height: 30px; border-radius: 10px; display: flex;
                      align-items: center; justify-content: center; font-size: 10px;
                      font-weight: bold; background: linear-gradient(145deg, #3b3f4a, #23262e);
                      border: 1px solid #3d4653; color: #e2e8f0; }
        .member-badge { color: #93a5bd; font-size: 9px; }
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

        /* One rule for every rail button, so a fourth one cannot drift from the other three. */
        .rail-button { width: 42px; height: 42px; border-radius: 14px; display: flex;
                       align-items: center; justify-content: center; background: #1b2029;
                       margin-top: 10px; cursor: pointer; }
        .rail-button:hover { background: #242a34; }
        .rail-button.hidden { display: none; }

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

        /* ── Management pages ──────────────────────────────────────────────────────────────
           One vocabulary for both the agents page and the users page. They are the same
           master-detail shape and are emitted by one template (ManagementPage), so a rule here
           lands on both by construction rather than by somebody remembering to copy it. */
        /* Laid out rather than placed at coordinates. Hybrid presentation means the logical
           viewport is whatever the window's aspect gives once the design size fits, so a card
           pinned to left:40 top:32 was only ever centred on one window shape. */
        .mgmt { position: absolute; left: 0; top: 0; width: 100%; height: 100%; display: flex; }
        .mgmt.hidden { display: none; }
        /* Dim rather than opaque: the room is still there, you have just stepped in front of it.
           It also has to be a real painted box, because it is what catches an outside click. */
        .mgmt-backdrop { position: absolute; left: 0; top: 0; width: 100%; height: 100%;
                         background: #05070ad0; }
        .mgmt-card { flex: 1; min-width: 0; margin: 32px 40px;
                     display: flex; flex-direction: row; background: #0f1319;
                     border: 1px solid #222932; border-radius: 16px; }

        .mgmt-list { width: 320px; display: flex; flex-direction: column;
                     border-right: 1px solid #1c222b; padding: 18px 14px; }
        .mgmt-list-head { display: flex; flex-direction: row; align-items: center; }
        .mgmt-list-heading { display: flex; flex-direction: column; flex: 1; min-width: 0; }
        .mgmt-title { font-size: 17px; font-weight: bold; color: #f3f5f7; }
        .mgmt-subtitle { font-size: 11px; color: #6b7482; margin-top: 2px; }
        .mgmt-new { padding: 8px 14px; font-size: 12px; font-weight: bold; background: #2563eb;
                    color: #ffffff; border: 1px solid #2563eb; border-radius: 10px;
                    text-align: center; }
        .mgmt-status { font-size: 10px; color: #5f6877; padding: 12px 2px 6px 2px; }
        .mgmt-rows { flex: 1; min-width: 0; overflow: scroll; }

        .mgmt-row { padding: 8px; border-radius: 10px; margin-bottom: 6px; background: #131820;
                    border: 1px solid #131820; cursor: pointer; }
        .mgmt-row:hover { background: #182029; }
        /* The selected row is the subject of everything on the right, so it is marked on its
           edge as well as its fill — a fill alone is easy to lose against a hover. */
        .mgmt-row.selected { background: #1b2430; border: 1px solid #2f6fd0; }
        .mgmt-row-inner { display: flex; flex-direction: row; align-items: center; }
        .mgmt-pfp { width: 32px; height: 32px; border-radius: 11px; display: flex;
                    align-items: center; justify-content: center; font-size: 10px;
                    font-weight: bold; background: #262d38; border: 1px solid #384150;
                    color: #cbd5e1; }
        .mgmt-row-main { display: flex; flex-direction: column; flex: 1; min-width: 0;
                         padding-left: 10px; }
        .mgmt-row-name { font-size: 13px; font-weight: bold; color: #f3f5f7; }
        .mgmt-row-detail { font-size: 10px; color: #8d97a6; margin-top: 1px; }
        /* The fingerprint when there is one; what is missing when there is not. An identity with
           no key cannot connect, and that is the thing an operator most needs to see. */
        .mgmt-state { font-size: 10px; color: #34d399; margin-top: 2px; }
        .mgmt-state.pending { color: #fbbf24; }

        .mgmt-pane { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 18px; }
        .mgmt-empty { flex: 1; display: flex; align-items: center; justify-content: center;
                      font-size: 12px; color: #4d5563; }
        .mgmt-empty.hidden { display: none; }
        .mgmt-detail { flex: 1; min-width: 0; display: flex; flex-direction: column; }
        .mgmt-detail.hidden { display: none; }
        .mgmt-detail-head { display: flex; flex-direction: row; align-items: center;
                            padding-bottom: 14px; border-bottom: 1px solid #1c222b; }
        .mgmt-remove { padding: 7px 14px; font-size: 12px; background: #2a1618; color: #fca5a5;
                       border: 1px solid #4c1d1d; border-radius: 10px; text-align: center; }
        .mgmt-remove.hidden { display: none; }

        .mgmt-fields { flex: 1; min-width: 0; overflow: scroll; padding-top: 14px; }
        .mgmt-field { display: flex; flex-direction: row; margin-bottom: 16px; }
        .mgmt-field.hidden { display: none; }
        .mgmt-label { width: 116px; font-size: 12px; font-weight: bold; color: #aab3c0;
                      padding-top: 7px; }
        .mgmt-control { flex: 1; min-width: 0; display: flex; flex-direction: column; }
        .mgmt-input { background: #0b0e13; color: #f3f5f7; border: 1px solid #252b35;
                      border-radius: 10px; padding: 8px 11px; font-size: 13px; }
        .mgmt-input:focus { border-color: #2f6fd0; }
        /* A name that cannot change is shown, not offered: a box you can click into and not
           change is a promise the screen cannot keep. */
        .mgmt-field.readonly .mgmt-input { display: none; }
        .mgmt-fixed { display: none; }
        .mgmt-field.readonly .mgmt-fixed { display: flex; align-items: center; background: #0b0e13;
                                           border: 1px solid #1b212a; border-radius: 10px;
                                           padding: 8px 11px; font-size: 13px; color: #8d97a6; }
        .mgmt-readonly { background: #0b0e13; border: 1px solid #1b212a; border-radius: 10px;
                         padding: 8px 11px; font-size: 13px; color: #8d97a6; }
        .mgmt-hint { font-size: 10px; color: #5f6877; margin-top: 5px; }

        .mgmt-choices { display: flex; flex-direction: row; }
        /* Zoom offers seven steps; on one row each would be too narrow to read. */
        .mgmt-choices.wrap { flex-wrap: wrap; }
        .mgmt-choice.compact { flex: 0 0 auto; width: 104px; margin-bottom: 8px; }
        .mgmt-choice { flex: 1; min-width: 0; display: flex; flex-direction: row;
                       background: #0b0e13; border: 1px solid #252b35; border-radius: 10px;
                       padding: 9px 10px; margin-right: 8px; cursor: pointer; }
        .mgmt-choice:hover { background: #121620; }
        .mgmt-choice.selected { background: #131c29; border: 1px solid #2f6fd0; }
        .mgmt-dot { width: 14px; height: 14px; border-radius: 7px; background: #0b0e13;
                    border: 1px solid #3a4351; margin-top: 1px; }
        /* The filled state is a smaller solid disc inside the ring, which is what a radio is —
           a border colour alone reads as hover on a card that already changes colour. */
        .mgmt-dot.on { background: #2f6fd0; border: 1px solid #2f6fd0; }
        .mgmt-choice-text { display: flex; flex-direction: column; flex: 1; min-width: 0;
                            padding-left: 9px; }
        .mgmt-choice-label { font-size: 12px; font-weight: bold; color: #e6eaf0; }
        .mgmt-choice-hint { font-size: 10px; color: #6b7482; margin-top: 2px; }

        .mgmt-inline { margin-top: 8px; padding: 7px 14px; font-size: 12px; background: #1b2029;
                       color: #f3f5f7; border: 1px solid #333a46; border-radius: 10px;
                       text-align: center; }
        .mgmt-inline.hidden { display: none; }

        .mgmt-footer { display: flex; flex-direction: row; align-items: center;
                       padding-top: 14px; border-top: 1px solid #1c222b; }
        .mgmt-dirty { flex: 1; min-width: 0; font-size: 11px; color: #fbbf24; }
        .mgmt-dirty.hidden { display: none; }
        /* Still flexes when hidden, or Cancel and Save would slide to the left edge the moment
           the form went clean. */
        .mgmt-dirty.hidden + .mgmt-cancel-agent, .mgmt-dirty.hidden + .mgmt-cancel-user { margin-left: auto; }
        .mgmt-cancel-agent, .mgmt-cancel-user { min-width: 90px; padding: 8px 16px; font-size: 12px;
                                                background: #1b2029; color: #f3f5f7;
                                                border: 1px solid #333a46; border-radius: 10px;
                                                text-align: center; margin-left: 8px; }
        .mgmt-save-agent, .mgmt-save-user { min-width: 120px; padding: 8px 16px; font-size: 12px;
                                            font-weight: bold; background: #2563eb; color: #ffffff;
                                            border: 1px solid #2563eb; border-radius: 10px;
                                            text-align: center; margin-left: 8px; }

        /* Settings has no list, so its card is the width of the pane and no more. */
        /* Centred by the overlay rather than by auto margins: CupriFace's flex resolves an auto
           margin on the main axis only, so `margin: auto` alone left this pinned to the top. */
        .settings-overlay { align-items: center; justify-content: center; }
        .settings-card { flex: 0 0 640px; height: 340px; }
        .settings-close { color: #f3f5f7; background: #1b2029; border: 1px solid #333a46; }

        /* ── Confirming something destructive ──────────────────────────────────────────────
           Above the page that raised it, and deliberately small: it asks one question about one
           named thing, and the page behind stays visible so the answer has its context. */
        .confirm { position: absolute; left: 0; top: 0; width: 100%; height: 100%; display: flex;
                   align-items: center; justify-content: center; }
        .confirm.hidden { display: none; }
        .confirm-backdrop { position: absolute; left: 0; top: 0; width: 100%; height: 100%;
                            background: #05070ac0; }
        .confirm-card { width: 440px; flex: 0 0 auto;
                        display: flex; flex-direction: column; background: #151920;
                        border: 1px solid #33202a; border-radius: 14px; padding: 18px; }
        .confirm-title { font-size: 15px; font-weight: bold; color: #f3f5f7; }
        .confirm-body { font-size: 12px; color: #98a2b0; margin-top: 8px; }
        .confirm-buttons { display: flex; flex-direction: row; justify-content: flex-end;
                           margin-top: 18px; }
        .confirm-cancel { min-width: 92px; padding: 8px 16px; font-size: 12px; background: #1b2029;
                          color: #f3f5f7; border: 1px solid #333a46; border-radius: 10px;
                          text-align: center; margin-right: 8px; }
        /* The destructive button is the one that is coloured, and it is never the default focus:
           the safe answer should be the one your hand is already on. */
        .confirm-go { min-width: 132px; padding: 8px 16px; font-size: 12px; font-weight: bold;
                      background: #b91c1c; color: #ffffff; border: 1px solid #b91c1c;
                      border-radius: 10px; text-align: center; }

        /* The secret is shown exactly once, so it gets the width of the pane rather than being
           tucked into the form that produced it. */
        .mgmt-secret { display: flex; flex-direction: column; background: #16301c;
                       border: 1px solid #2f6b3f; border-radius: 12px; padding: 10px 12px;
                       margin-top: 14px; }
        .mgmt-secret.hidden { display: none; }
        .mgmt-secret-note { font-size: 11px; color: #a7e3bb; }
        .mgmt-secret-row { display: flex; flex-direction: row; align-items: center; margin-top: 6px; }
        .mgmt-secret-value { flex: 1; min-width: 0; font-size: 13px; color: #ffffff; }
        .mgmt-copy { min-width: 64px; padding: 5px 12px; font-size: 12px; background: #1b2029;
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

        // Page zoom is the mode that reflows rather than only magnifying, which is what makes a
        // zoomed window still use its full width instead of scrolling sideways. Enabled here so
        // it is the behaviour by default rather than something to discover.
        doc.PageZoomEnabled = true;
        doc.Zoom = InitialZoom;
        ViewModel.SetZoom(doc.Zoom);

        // The wheel and the keyboard reach zoom without this page, so the page has to follow
        // them rather than be the only thing that knows.
        doc.ZoomChanged += zoom =>
        {
            ViewModel.SetZoom(zoom);
            ZoomChanged(zoom);
        };

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

        // ---- The management pages ----
        //
        // Agents and users are the same page twice, so these come in pairs. The pairs are kept
        // adjacent on purpose: a handler added to one and forgotten on the other is the drift
        // this rewrite exists to stop.

        doc.OnAction("data-agents-open", _unused =>
        {
            ViewModel.ShowAgentsPanel(true);
            _ = AgentsListAsync();
            doc.Refresh();
            return true;
        });

        doc.OnAction("data-users-open", _unused =>
        {
            ViewModel.ShowUsersPanel(true);
            _ = UsersListAsync();
            doc.Refresh();
            return true;
        });

        // Clicking away closes. The backdrop is painted under the card, so a click that lands on
        // it is one that missed the card.
        doc.OnAction("data-agents-close", _unused =>
        {
            ViewModel.ShowAgentsPanel(false);
            doc.Refresh();
            return true;
        });

        doc.OnAction("data-users-close", _unused =>
        {
            ViewModel.ShowUsersPanel(false);
            doc.Refresh();
            return true;
        });

        doc.OnAction("data-agent-new", _unused =>
        {
            ViewModel.NewAgent();
            doc.Refresh();
            return true;
        });

        doc.OnAction("data-user-new", _unused =>
        {
            ViewModel.NewUser();
            doc.Refresh();
            return true;
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

        doc.OnAction("data-agent-locality", e =>
        {
            ViewModel.ChooseAgentLocality(e.Value ?? "local");
            doc.Refresh();
            return true;
        });

        doc.OnAction("data-agent-clearance", e =>
        {
            ViewModel.ChooseAgentClearance(e.Value ?? "sensitive");
            doc.Refresh();
            return true;
        });

        doc.OnAction("data-agent-delegator", e =>
        {
            ViewModel.ChooseAgentDelegator(e.Value ?? "auto");
            doc.Refresh();
            return true;
        });

        doc.OnAction("data-user-role", e =>
        {
            ViewModel.ChooseUserRole(e.Value ?? "member");
            doc.Refresh();
            return true;
        });

        // Save means "create" or "update" depending on which state the pane is in, and the
        // button already says which — reading the mode here is what keeps those two agreeing.
        doc.OnClick(".mgmt-save-agent", _unused =>
        {
            if (ViewModel.ReadAgentForm() is not { } form)
            {
                doc.Refresh();
                return;
            }

            _ = ViewModel.AgentIsNew ? AgentCreateAsync(form) : AgentSaveAsync(form);
        });

        doc.OnClick(".mgmt-save-user", _unused =>
        {
            if (ViewModel.ReadUserForm() is not { } form)
            {
                doc.Refresh();
                return;
            }

            _ = ViewModel.UserIsNew
                ? UserCreateAsync(form.Username, form.IsAdmin)
                : UserSetAdminAsync(form.Username, form.IsAdmin);
        });

        doc.OnClick(".mgmt-cancel-agent", _unused =>
        {
            ViewModel.ClearAgentDetail();
            ViewModel.ClearAdminCode();
            doc.Refresh();
        });

        doc.OnClick(".mgmt-cancel-user", _unused =>
        {
            ViewModel.ClearUserDetail();
            ViewModel.ClearAdminCode();
            doc.Refresh();
        });

        // Removal asks first. The button raises the question; the dialog is what acts.
        doc.OnClick(".mgmt-remove", _unused =>
        {
            if (ViewModel.Model.AgentSelected.Length > 0)
            {
                ViewModel.ConfirmRemoveAgent();
            }
            else if (ViewModel.Model.UserSelected.Length > 0)
            {
                ViewModel.ConfirmRemoveUser();
            }

            doc.Refresh();
        });

        doc.OnClick(".confirm-go", _unused =>
        {
            // Taking it clears the dialog, so a second click cannot run the same removal twice.
            if (ViewModel.TakeConfirmed() is not { } confirmed || confirmed.Subject.Length == 0)
            {
                doc.Refresh();
                return;
            }

            _ = confirmed.IsAgent
                ? AgentRemoveAsync(confirmed.Subject)
                : UserRemoveAsync(confirmed.Subject);
            doc.Refresh();
        });

        doc.OnClick(".confirm-cancel", _unused =>
        {
            ViewModel.CancelConfirm();
            doc.Refresh();
        });

        // Clicking away from a question is not an answer to it, so it cancels.
        doc.OnAction("data-confirm-no", _unused =>
        {
            ViewModel.CancelConfirm();
            doc.Refresh();
            return true;
        });

        // ---- Settings ----

        doc.OnAction("data-settings-open", _unused =>
        {
            ViewModel.ShowSettingsPanel(true);
            ViewModel.SetZoom(doc.Zoom);
            doc.Refresh();
            return true;
        });

        doc.OnAction("data-settings-close", _unused =>
        {
            ViewModel.ShowSettingsPanel(false);
            doc.Refresh();
            return true;
        });

        doc.OnClick(".settings-close", _unused =>
        {
            ViewModel.ShowSettingsPanel(false);
            doc.Refresh();
        });

        doc.OnAction("data-zoom", e =>
        {
            if (!float.TryParse(e.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var zoom))
            {
                return false;
            }

            // Set it, then read it back: CupriFace clamps to its own bounds, and the page should
            // show what is in force rather than what was asked for.
            doc.Zoom = zoom;
            ViewModel.SetZoom(doc.Zoom);
            ZoomChanged(doc.Zoom);
            doc.Refresh();
            return true;
        });

        // Both inline actions share a class, and only one page is ever open, so which one this
        // is follows from which pane has a subject.
        doc.OnClick(".mgmt-inline", _unused =>
        {
            if (ViewModel.Model.AgentSelected.Length > 0)
            {
                _ = AgentReissueAsync(ViewModel.Model.AgentSelected);
            }
            else if (ViewModel.Model.UserSelected.Length > 0)
            {
                _ = UserResetAsync(ViewModel.Model.UserSelected);
            }
        });

        // The secret exists in readable form for as long as this page is open and no longer, so
        // copying it is the whole point of showing it.
        doc.OnClick(".mgmt-copy", _unused =>
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

        return Presentation(width, height, _doc?.Zoom ?? 1f);
    }

    /// <summary>
    /// The size Banter lays itself out at, and the scale it is painted with.
    ///
    /// <para><b>Hybrid</b> (CupriFace 0.18.0) rather than responsive: it picks the largest scale at
    /// which <see cref="DesignWidth"/> x <see cref="DesignHeight"/> still fits the window, then
    /// reflows whatever is left over. Responsive layout alone treats a 27-inch monitor as
    /// "room for more" and paints everything at the same physical size it had on a laptop, which
    /// is why a chat window on a big screen ends up a wall of tiny text; fixed scaling would go the
    /// other way and letterbox. Hybrid does both: on a 2560x1440 window the design fits 1.8 times
    /// over, so everything is painted 1.8x and the surplus width becomes a wider timeline rather
    /// than empty margin.</para>
    ///
    /// <para>The user's zoom multiplies that base rather than replacing it, so 100% means "what
    /// this screen deserves" instead of "one CSS pixel per device pixel" — which on a 4K panel is
    /// not a setting anybody wants.</para>
    /// </summary>
    public static PresentInfo Presentation(float width, float height, float zoom)
    {
        var hybrid = PresentInfo.Hybrid(width, height, DesignWidth, DesignHeight);
        return PresentInfo.Zoom(width, height, hybrid.Scale * zoom);
    }

    /// <summary>
    /// The window Banter is drawn for. Not a minimum and not a maximum — the size whose scale is
    /// called 100%, chosen because it is the smallest window the four-column layout (rail,
    /// sidebar, timeline, roster) still reads well in.
    /// </summary>
    public const float DesignWidth = 1280f;

    public const float DesignHeight = 800f;

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
