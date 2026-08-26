using Banter.App;
using Banter.Client.Core;
using Banter.Protocol.Transport;
using Banter.Transport.CupriNet;
using CupriFace.Shell;

// The desktop head. Deliberately thin: resolve settings, connect, hand the wiring to
// BanterChatSession, and run the shared CupriApp. Everything visible lives in Banter.App.

var cli = ParseArgs(args);
if (cli is null)
{
    Console.WriteLine($"""
        banter - Banter desktop client

          banter [--server <uri>] [--user <name>] [--pass <secret>] [--room #main] [--rooms #a,#b]
                 [--settings <path>] [--save]

        Server, user and rooms are remembered in:
          {BanterSettings.DefaultPath}
        so later runs need only a password. Anything given on the command line overrides
        what is stored; --save writes the result back after a successful connect.

        Secrets are never written to that file. Supply them with --pass, or via the
        BANTER_PASS and BANTER_WATCHWORD environment variables.

        Transport is chosen by URI scheme:
          tcp://host:port           plain TCP
          cupri://<intonation-uri>  CupriNet mesh (paste the mesh-magnet link)
        """);
    return 0;
}

var (argServer, argUser, argPass, argRooms, settingsPath, save) = cli.Value;

var stored = BanterSettings.Load(settingsPath, p => Console.Error.WriteLine($"warning: {p}"));
var settings = stored.With(argServer, argUser, argRooms);

var pass = argPass
    ?? Environment.GetEnvironmentVariable("BANTER_PASS");

if (!settings.IsComplete || string.IsNullOrEmpty(pass))
{
    Console.Error.WriteLine(
        settings.IsComplete
            ? "error: no password. Pass --pass or set BANTER_PASS."
            : "error: no server/user configured. Run once with --server and --user (add --save to remember).");
    return 2;
}

var server = new Uri(settings.Server);
var rooms = settings.Rooms.Count > 0 ? settings.Rooms : ["#main"];

IBanterClientTransport transport = server.Scheme == "tcp"
    ? new TcpBanterTransport()
    : new CupriNetBanterTransport(new CupriNetTransportOptions
    {
        // Mesh identity lives beside the settings file so a profile is one folder.
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Banter", "mesh"),
        // The channel secret shared with the server operator. Falls back to the account
        // password so a single-secret setup works without extra configuration.
        Watchword = Environment.GetEnvironmentVariable("BANTER_WATCHWORD") ?? pass,
        EnableLanDiscovery = true,
    });

var vm = new ChatViewModel { RoomScrollback = settings.Scrollback };
vm.SetStatus("Connecting...", connected: false);

// BanterClient reconnects an *established* session but lets the first connect throw, which is
// right for the library and wrong for a desktop app: the server may simply not be up yet (a
// debugger launching both at once, a machine still booting). So retry briefly here, and keep
// failing fast on a rejected credential, which no amount of waiting will fix.
var client = await ConnectWithRetryAsync();
if (client is null)
{
    return 1;
}

async Task<BanterClient?> ConnectWithRetryAsync()
{
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
    var delay = TimeSpan.FromMilliseconds(250);
    while (true)
    {
        try
        {
            return await BanterClient.ConnectAsync(transport, server, settings.User, pass);
        }
        catch (BanterAuthException ex)
        {
            Console.Error.WriteLine($"error: {server} rejected the credentials for '{settings.User}': {ex.Message}");
            return null;
        }
        catch (Exception ex) when (DateTimeOffset.UtcNow < deadline)
        {
            Console.Error.WriteLine($"waiting for {server}: {ex.Message}");
            await Task.Delay(delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2_000));
        }
        catch (Exception ex)
        {
            // Fail rather than opening a window that can only show an error: the user is at a
            // terminal, and an unreachable server is worth a non-zero exit code.
            Console.Error.WriteLine($"error: could not connect to {server}: {ex.Message}");
            return null;
        }
    }
}

await using var _ = client;
using var session = new BanterChatSession(client, vm);

vm.Post(() =>
{
    vm.SetNick(client.Nick);
    vm.SetStatus("Connected", connected: true);
});

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

// Only persist a configuration that actually worked, and only when asked.
if (save)
{
    settings.TrySave(settingsPath, p => Console.Error.WriteLine($"warning: {p}"));
}

var app = new BanterChatApp(vm)
{
    SendAsync = session.SendAsync,
    // Room switching is local — the backlog is already held per room, and history was
    // back-filled at join.
    RoomSelected = _ => { },
    LoadOlderAsync = room => session.LoadOlderAsync(room, settings.HistoryPageSize),
    CommandAsync = session.CommandAsync,
    DownloadAsync = session.DownloadAsync,
    JoinRoomAsync = room => session.JoinAsync(room, settings.HistoryPageSize),
};

DesktopHost.Run(app, _ => { });
return 0;

static (string? Server, string? User, string? Pass, string[]? Rooms, string? SettingsPath, bool Save)? ParseArgs(string[] argv)
{
    string? server = null, user = null, pass = null, settingsPath = null;
    var rooms = new List<string>();
    var save = false;

    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--save": save = true; break;
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

    return (server, user, pass, rooms.Count > 0 ? rooms.ToArray() : null, settingsPath, save);
}
