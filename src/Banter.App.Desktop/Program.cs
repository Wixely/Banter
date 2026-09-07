using Banter.App;
using Banter.Client.Core;
using Banter.Protocol.Transport;
using Banter.Transport.CupriNet;
using CupriFace.Shell;

// The desktop head. Deliberately thin: resolve settings, run the shared CupriApp, and hand the
// wiring to BanterChatSession. Everything visible lives in Banter.App.
//
// The window opens first and the connection is made behind it, which is the opposite of how this
// used to work. Given no arguments it used to print "no server/user configured" and exit 2 — to a
// console that does not exist when a windowed application is started from a shortcut or a file
// manager, so the only observable behaviour was that nothing happened. A sign-in screen already
// existed for Android; this head now uses it too, and the command line became a way to skip it.

var cli = ParseArgs(args);
if (cli is null)
{
    Console.WriteLine($"""
        banter - Banter desktop client

          banter [--server <uri>] [--user <name>] [--pass <secret>] [--room #main] [--rooms #a,#b]
                 [--settings <path>] [--save] [--forget]

        With no arguments it opens on the sign-in screen, or signs straight in if it was told to
        stay signed in last time. Anything given here overrides what is stored and skips that
        screen; --save writes server, user and rooms back after a successful connect, and
        --forget discards a remembered password before starting.

        Preferences (server, user, rooms, voice, zoom):
          {BanterSettings.DefaultPath}
        Remembered password, written only when signing in on screen:
          {StoredCredentials.DefaultPath}
        {Banter.App.Desktop.DpapiSecretProtector.StorageDescription()}

        A password may also come from --pass or the BANTER_PASS environment variable, which is
        what a scripted or debugger launch should use - neither is ever written to disk.

        Transport is chosen by URI scheme:
          tcp://host:port           plain TCP
          cupri://<intonation-uri>  CupriNet mesh (paste the mesh-magnet link)
        """);
    return 0;
}

var (argServer, argUser, argPass, argRooms, settingsPath, save, forget) = cli.Value;

void Warn(string message) => Console.Error.WriteLine($"warning: {message}");

var settings = BanterSettings.Load(settingsPath, Warn).With(argServer, argUser, argRooms);

// Where the remembered password lives, and what wraps it. Derived from the settings path so a
// debugger profile keeps its own: several heads pointed at scratch settings files would otherwise
// share one credential in the real profile, and alice would sign in as whoever ran last.
var credentialsPath = StoredCredentials.PathFor(settingsPath);
var protector = Banter.App.Desktop.DpapiSecretProtector.ForThisMachine();

if (forget)
{
    StoredCredentials.TryDelete(credentialsPath, Warn);
}

var remembered = forget ? null : StoredCredentials.Load(credentialsPath, protector, Warn);

// An explicit password never comes from disk, and a remembered one only counts when it belongs to
// the account about to be used: `banter --user bob` after signing in as alice must ask for bob's
// password rather than quietly trying alice's and reporting a rejection.
var startPassword =
    argPass
    ?? Environment.GetEnvironmentVariable("BANTER_PASS")
    ?? (remembered is not null
        && (argServer is null || Same(argServer, remembered.Server))
        && (argUser is null || Same(argUser, remembered.User))
            ? remembered.Password
            : null);

// A remembered credential is also the answer to "which server", so a client that has signed in
// before opens with no arguments at all.
if (!settings.IsComplete && remembered is not null && argServer is null && argUser is null)
{
    settings = settings with { Server = remembered.Server, User = remembered.User };
}

var rooms = settings.Rooms.Count > 0 ? settings.Rooms : ["#main"];

var vm = new ChatViewModel { RoomScrollback = settings.Scrollback };

// Voice is built from settings alone, so it exists before there is a session to attach it to.
// That matters now that the window opens first: the settings page lists the voices this machine
// can speak with, and it would otherwise be empty until somebody signed in.
var voice = Banter.App.Desktop.DesktopVoice.TryBuild(settings.Voice, warn: m => Warn($"voice: {m}"));
var preparingSpeech = Task.CompletedTask;

// The live session. Null until a sign-in succeeds, and null again after signing out, which is why
// every callback below goes through it rather than capturing one.
BanterClient? client = null;
BanterChatSession? session = null;
Banter.App.Desktop.DesktopHotkey? hotkey = null;

var filePicker = new Banter.App.Desktop.SystemFilePicker();
if (!filePicker.IsSupported)
{
    Console.Error.WriteLine("no file dialog on this platform; use /upload <path>.");
}

var app = new BanterChatApp(vm)
{
    ConnectAsync = (server, user, password) => SignInAsync(server, user, password, persist: true),
    SignOutAsync = SignOutAsync,

    // Flash the taskbar when named, unless the window is already in front - which
    // TaskbarAttention decides, because it is the only thing here that knows. The count is
    // ignored: one flashing button says "somebody wants you" whether it was one message or six,
    // and there is no louder version of that to escalate to.
    MentionedYou = _ =>
    {
        if (settings.FlashOnMention)
        {
            Banter.App.Desktop.TaskbarAttention.Raise();
        }
    },

    SendAsync = (room, text) => session?.SendAsync(room, text) ?? Task.CompletedTask,
    ReplyAsync = (room, text, replyTo) => session?.SendAsync(room, text, replyTo) ?? Task.CompletedTask,
    AnswerAsync = answer => session?.AnswerAsync(answer) ?? Task.CompletedTask,
    // Room switching is local — the backlog is already held per room, and history was
    // back-filled at join.
    RoomSelected = _ => { },
    LoadOlderAsync = room => session?.LoadOlderAsync(room, settings.HistoryPageSize) ?? Task.CompletedTask,
    CommandAsync = (room, line) => session?.CommandAsync(room, line) ?? Task.CompletedTask,
    EditAsync = (room, id, text) => session?.EditAsync(room, id, text) ?? Task.CompletedTask,
    DeleteAsync = (room, id) => session?.DeleteAsync(room, id) ?? Task.CompletedTask,
    DownloadAsync = fileId => session?.DownloadAsync(fileId) ?? Task.CompletedTask,
    JoinRoomAsync = room => session?.JoinAsync(room, settings.HistoryPageSize) ?? Task.CompletedTask,
    ToolsOpenAsync = agent => session?.LoadToolsAsync(agent) ?? Task.CompletedTask,
    ToolsSaveAsync = (agent, tools) => session?.SaveToolsAsync(agent, tools) ?? Task.CompletedTask,
    AgentsListAsync = () => session?.LoadAgentIdentitiesAsync() ?? Task.CompletedTask,
    AgentCreateAsync = name => session?.CreateAgentIdentityAsync(name) ?? Task.CompletedTask,
    AgentSaveAsync = form => session?.SaveAgentIdentityAsync(form) ?? Task.CompletedTask,
    AgentReissueAsync = name => session?.ReissueAgentIdentityAsync(name) ?? Task.CompletedTask,
    AgentRemoveAsync = name => session?.RemoveAgentIdentityAsync(name) ?? Task.CompletedTask,
    UsersListAsync = () => session?.LoadUsersAsync() ?? Task.CompletedTask,
    WorkListAsync = () => session?.LoadAllTasksAsync() ?? Task.CompletedTask,
    UserCreateAsync = (name, admin) => session?.CreateUserAccountAsync(name, admin) ?? Task.CompletedTask,
    UserResetAsync = name => session?.ResetUserPasswordAsync(name) ?? Task.CompletedTask,
    UserSetAdminAsync = (name, admin) => session?.SetUserAdminAsync(name, admin) ?? Task.CompletedTask,
    UserRemoveAsync = name => session?.RemoveUserAccountAsync(name) ?? Task.CompletedTask,

    Clipboard = new Banter.App.Desktop.SystemClipboard(),
    StayInTray = settings.StayInTray,
    InitialZoom = settings.Zoom,
    Voices = [.. (voice?.Voices ?? []).Select(v => (v.Id, v.DisplayName))],
    FilePicker = filePicker,
    // The picker hands back a path; quoting it is what lets a chosen file have spaces in its name.
    AttachAsync = (room, path) => session?.UploadAsync(room, $"\"{path}\"") ?? Task.CompletedTask,
    VoiceToggleAsync = open => session?.SetVoiceOpenAsync(open) ?? Task.CompletedTask,
    ReadbackChangedAsync = policy => session?.SetReadbackAsync(policy) ?? Task.CompletedTask,

    // Pinning takes effect on the next thing spoken, not the next launch: choosing a voice and
    // then having to restart to hear it is how you end up unable to tell whether it worked.
    VoicePinned = (nick, voiceId) =>
    {
        if (voiceId is { Length: > 0 })
        {
            voice?.Assignment?.Pin(nick, voiceId);
        }
        else
        {
            voice?.Assignment?.Unpin(nick);
        }

        var pins = new Dictionary<string, string>(vm.VoicePins, StringComparer.OrdinalIgnoreCase);
        settings = settings with { Voice = settings.Voice with { Voices = pins } };
        settings.TrySave(settingsPath, Warn);
    },

    // The engine and the endpoints are read at startup, so a change here is saved and takes
    // effect on the next launch. Said on the page rather than pretended otherwise.
    VoiceSettingsChanged = () =>
    {
        settings = settings with
        {
            FlashOnMention = vm.FlashOnMention,
            Voice = settings.Voice with
            {
                Engine = vm.ChosenTranscribe,
                Language = vm.Model.VoiceLanguage.Trim(),
                Vocabulary = vm.Model.VoiceVocabulary.Trim(),
                Endpoint = vm.Model.VoiceEndpoint.Trim(),
                WyomingTts = vm.Model.VoiceWyomingTts.Trim(),
                AutoSubmit = vm.ChosenAutoSubmit,
                // Read rather than taken: the box is free text, and a delay that cannot be parsed
                // keeps the value it had rather than becoming zero, which is the one direction of
                // this setting that sends something before anybody can stop it.
                AutoSubmitDelaySeconds = vm.ReadAutoSubmitDelay(),
            },
        };

        settings.TrySave(settingsPath, Warn);
    },

    // Zoom is a preference about eyesight and monitors, so it is written the moment it changes
    // rather than only when --save is passed: nobody expects to have to re-choose it.
    ZoomChanged = zoom =>
    {
        settings = settings with { Zoom = zoom };
        settings.TrySave(settingsPath, Warn);
    },
};

if (filePicker.IsSupported)
{
    vm.Post(vm.EnableAttach);
}

// Fill the settings page with what was loaded and what this machine can actually speak with.
// ReviewBeforeSend is the old switch and still wins: a profile that asked never to send by
// itself keeps that, whatever the new default says.
vm.SetAutoSubmit(settings.Voice.AutoSubmit && !settings.Voice.ReviewBeforeSend, settings.Voice.AutoSubmitDelaySeconds);
vm.SetFlashOnMention(settings.FlashOnMention);
vm.SetVoiceSettings(
    settings.Voice.Engine,
    settings.Voice.Language,
    settings.Voice.Vocabulary,
    settings.Voice.Endpoint,
    settings.Voice.WyomingTts,
    [.. (voice?.Voices ?? []).Select(v => (v.Id, v.DisplayName))],
    settings.Voice.Voices);

// Either sign straight in, or open on the screen that asks. Started rather than awaited: the
// window must come up either way, and a server that is still booting should be waited for behind
// a visible client rather than in front of a missing one.
if (settings.IsComplete && !string.IsNullOrEmpty(startPassword))
{
    vm.SetStatus("Connecting...", connected: false);
    _ = SignInAsync(settings.Server, settings.User, startPassword, persist: false, retry: true);
}
else
{
    vm.SetStatus("Not connected", connected: false);
    vm.ShowConnect(settings.Server, settings.User, Banter.App.Desktop.DpapiSecretProtector.StorageDescription());
}

DesktopHost.Run(app, _ => { });

hotkey?.Dispose();
session?.Dispose();

if (client is not null)
{
    await client.DisposeAsync();
}

if (voice is not null)
{
    await preparingSpeech;
    await voice.DisposeAsync();
}

return 0;

/// <summary>
/// Connects, and on success wires everything that needs a live session behind it.
///
/// <para>Every failure lands on the sign-in screen rather than throwing or exiting: by the time
/// this runs there is a window, and the person in front of it is looking at the form that caused
/// the failure and is the only place they can do anything about it.</para>
/// </summary>
async Task SignInAsync(string serverText, string user, string password, bool persist, bool retry = false)
{
    if (!Uri.TryCreate(serverText, UriKind.Absolute, out var server))
    {
        vm.Post(() => vm.ConnectFailed($"'{serverText}' is not a server address."));
        return;
    }

    // Built-in schemes first; anything else is the mesh, which lives outside Banter.Protocol.
    var transport = BanterTransports.TryClient(server) ?? BuildCupriNet(password);

    BanterClient connected;
    try
    {
        // BanterClient reconnects an *established* session but lets the first connect throw, which
        // is right for the library and wrong here when nobody asked: a debugger launching client
        // and server at once, or a machine still booting, is a wait rather than a failure. Somebody
        // who just pressed Connect gets told immediately instead.
        connected = retry
            ? await ConnectWithRetryAsync(transport, server, user, password)
            : await BanterClient.ConnectAsync(transport, server, user, password);
    }
    catch (BanterAuthException ex)
    {
        // The stored password is the likeliest thing that just went stale — a password changed on
        // another machine, or an account removed. Drop it, or every launch retries the same
        // rejection and the sign-in screen never gets a chance to fix it.
        StoredCredentials.TryDelete(credentialsPath, Warn);
        vm.Post(() => vm.SignedOut(serverText, user, $"Refused: {ex.Message}"));
        return;
    }
    catch (Exception ex)
    {
        vm.Post(() => vm.SignedOut(serverText, user, ex.Message));
        return;
    }

    client = connected;
    session = new BanterChatSession(connected, vm);

    vm.Post(() =>
    {
        vm.SetNick(connected.Nick);
        vm.SetStatus("Connected", connected: true);
        vm.Connected(serverText, user);
    });

    // Remembered only once it worked. `persist` is what separates signing in on screen from being
    // handed a password on the command line: the second is a scripted or debugger launch, and
    // writing its credential into the profile is not what was asked for.
    settings = settings with { Server = serverText, User = user };
    if (persist)
    {
        settings.TrySave(settingsPath, Warn);
        new StoredCredentials(serverText, user, password).TrySave(credentialsPath, protector, Warn);
    }
    else if (save)
    {
        settings.TrySave(settingsPath, Warn);
    }

    foreach (var room in rooms)
    {
        try
        {
            await session.JoinAsync(room, settings.HistoryPageSize);
        }
        catch (Exception ex)
        {
            vm.Post(() => vm.System(room, $"could not join {room}: {ex.Message}"));
        }
    }

    // Probe for tools once, so the "Manage tools" control appears only for an account the server
    // would actually let manage them. Anyone else is simply refused here and never sees the button.
    await session.LoadToolsAsync("");

    AttachVoice();
}

/// <summary>
/// Attaches voice and the global push-to-talk key to the session that has just started. Both are
/// per-session: the hotkey's destination is a room in the session it was registered for, so it is
/// registered here and released on sign-out rather than held for the life of the process.
/// </summary>
void AttachVoice()
{
    if (voice is null || session is null)
    {
        return;
    }

    vm.ReviewBeforeSend = settings.Voice.ReviewBeforeSend;
    vm.Post(() => vm.SetReadback(voice.Policy));
    session.AttachVoice(voice.Session, voice.Readback);

    // Started, not awaited: the first run downloads a speech model, and the room should be usable
    // while that happens. Progress lands in the timeline. Only ever started once, however many
    // times somebody signs in and out.
    if (preparingSpeech == Task.CompletedTask)
    {
        preparingSpeech = voice.PrepareAsync(
            m => vm.Post(() => vm.System(vm.Model.ActiveRoom, $"[voice] {m}")));
    }

    // Decided here rather than read off the screen: the point of a global hotkey is that the app
    // is not on screen when it is pressed.
    var homeRoom = settings.Voice.HomeRoom.Length > 0 ? settings.Voice.HomeRoom : rooms[0];
    var live = session;
    hotkey ??= Banter.App.Desktop.DesktopHotkey.TryRegister(
        settings.Voice.Hotkey,
        open => live.SetVoiceOpenAsync(open, homeRoom),
        warn: m => vm.Post(() => vm.System(vm.Model.ActiveRoom, $"[voice] {m}")));

    if (hotkey is not null)
    {
        vm.Post(() => vm.System(homeRoom, $"[voice] hold {hotkey.Display} to talk to {homeRoom}."));
    }
}

/// <summary>
/// Ends the session and forgets the password, leaving the sign-in screen filled in with the server
/// and account that were just used — the common reason to be here is a different account on the
/// same server, and retyping the address for that would be a poor reward.
/// </summary>
async Task SignOutAsync()
{
    var was = session;
    var wasClient = client;
    session = null;
    client = null;

    // Released before the session it points at goes away: it captures that session to send what it
    // hears, and a chord pressed afterwards would be reaching into a disposed connection.
    hotkey?.Dispose();
    hotkey = null;

    was?.Dispose();
    if (wasClient is not null)
    {
        await wasClient.DisposeAsync();
    }

    StoredCredentials.TryDelete(credentialsPath, Warn);
    vm.Post(() => vm.SignedOut(settings.Server, settings.User));
}

IBanterClientTransport BuildCupriNet(string password) =>
    new CupriNetBanterTransport(new CupriNetTransportOptions
    {
        // Mesh identity lives beside the settings file so a profile is one folder.
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Banter", "mesh"),
        // The channel secret shared with the server operator. Falls back to the account
        // password so a single-secret setup works without extra configuration.
        Watchword = Environment.GetEnvironmentVariable("BANTER_WATCHWORD") ?? password,
        EnableLanDiscovery = true,
    });

async Task<BanterClient> ConnectWithRetryAsync(
    IBanterClientTransport transport, Uri server, string user, string password)
{
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
    var delay = TimeSpan.FromMilliseconds(250);
    while (true)
    {
        try
        {
            return await BanterClient.ConnectAsync(transport, server, user, password);
        }
        catch (BanterAuthException)
        {
            throw;                                  // no amount of waiting fixes a wrong password
        }
        catch (Exception ex) when (DateTimeOffset.UtcNow < deadline)
        {
            vm.Post(() => vm.SetStatus($"Waiting for {server.Host}...", connected: false));
            Console.Error.WriteLine($"waiting for {server}: {ex.Message}");
            await Task.Delay(delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2_000));
        }
    }
}

static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

static (string? Server, string? User, string? Pass, string[]? Rooms, string? SettingsPath, bool Save, bool Forget)?
    ParseArgs(string[] argv)
{
    string? server = null, user = null, pass = null, settingsPath = null;
    var rooms = new List<string>();
    var save = false;
    var forget = false;

    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--save": save = true; break;
            case "--forget": forget = true; break;
            case "-h" or "--help": return null;
            case "--server" when i + 1 < argv.Length: server = argv[++i]; break;
            case "--user" when i + 1 < argv.Length: user = argv[++i]; break;
            case "--pass" when i + 1 < argv.Length: pass = argv[++i]; break;
            case "--room" when i + 1 < argv.Length: rooms.Add(argv[++i]); break;
            case "--settings" when i + 1 < argv.Length: settingsPath = argv[++i]; break;
            case "--rooms" when i + 1 < argv.Length:
                rooms.AddRange(argv[++i].Split(',', StringSplitOptions.RemoveEmptyEntries));
                break;
        }
    }

    return (server, user, pass, rooms.Count > 0 ? rooms.ToArray() : null, settingsPath, save, forget);
}
