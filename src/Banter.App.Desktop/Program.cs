using Banter.App;
using Banter.Client.Core;
using Banter.Protocol.Transport;
using Banter.Transport.CupriNet;
using CupriFace.Shell;

// The desktop head. Deliberately thin: pick a transport, connect, hand the wiring to
// BanterChatSession, and run the shared CupriApp. Everything visible lives in Banter.App.

var opts = ParseArgs(args);
if (opts is null)
{
    Console.WriteLine("""
        banter - Banter desktop client

          banter --server <uri> --user <name> --pass <secret> [--room #main] [--rooms #a,#b]

        Transport is chosen by URI scheme:
          tcp://host:port     plain TCP
          cupri://<intonation-uri>  CupriNet mesh (pass the mesh-magnet link)
        """);
    return 0;
}

var (server, user, pass, rooms) = opts.Value;

IBanterClientTransport transport = server.Scheme == "tcp"
    ? new TcpBanterTransport()
    : new CupriNetBanterTransport(new CupriNetTransportOptions
    {
        // Mesh identity lives beside the app's other state so a reinstall doesn't reset it.
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Banter", "mesh"),
        // The watchword is the channel secret shared with the server operator; reuse the
        // account password unless one is given explicitly.
        Watchword = Environment.GetEnvironmentVariable("BANTER_WATCHWORD") ?? pass,
        EnableLanDiscovery = true,
    });

var vm = new ChatViewModel();
vm.SetStatus("Connecting...", connected: false);

BanterClient client;
try
{
    client = await BanterClient.ConnectAsync(transport, server, user, pass);
}
catch (Exception ex)
{
    // Fail here rather than opening a window that can only show an error: the user is at a
    // terminal, and a bad password or unreachable server is worth an exit code.
    Console.Error.WriteLine($"error: could not connect to {server}: {ex.Message}");
    return 1;
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
        await session.JoinAsync(room);
    }
    catch (Exception ex)
    {
        vm.Post(() => vm.System(room, $"could not join {room}: {ex.Message}"));
    }
}

var app = new BanterChatApp(vm)
{
    SendAsync = session.SendAsync,
    // Room switching is local — the backlog is already held per room, and history was
    // back-filled at join. A future paged scrollback hooks in here.
    RoomSelected = _ => { },
};

DesktopHost.Run(app, _ => { });
return 0;

static (Uri Server, string User, string Pass, string[] Rooms)? ParseArgs(string[] argv)
{
    string? server = null, user = null, pass = null;
    var rooms = new List<string>();
    for (var i = 0; i < argv.Length - 1; i++)
    {
        switch (argv[i])
        {
            case "--server": server = argv[++i]; break;
            case "--user": user = argv[++i]; break;
            case "--pass": pass = argv[++i]; break;
            case "--room": rooms.Add(argv[++i]); break;
            case "--rooms": rooms.AddRange(argv[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
        }
    }

    if (server is null || user is null || pass is null || !Uri.TryCreate(server, UriKind.Absolute, out var uri))
    {
        return null;
    }

    if (rooms.Count == 0)
    {
        rooms.Add("#main");
    }

    return (uri, user, pass, rooms.ToArray());
}
