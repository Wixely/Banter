using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using System.Net;
using Banter.Transport.Shrine;
using CupriNet.Nodestar;
using CupriNet.Nodestar.WebRtc;

// A Banter server that lives on a CupriNet node instead of a socket (PLAN §2.5). The node serves
// the clearnet on-ramp; the room runs on an L2 conduit behind it, and every Banter verb rides that
// conduit unchanged because the conduit is presented as an IBanterConnection.
//
// Separate from Banter.Server's own Program on purpose: this consumes a local build of
// CupriNet.Nodestar that is not on the feed yet, and the shipping server must keep restoring
// without it.

// The link as a code a phone can read, which is the only humane way to get ~380 characters of
// base64 onto one. On by default because the moment you need it is the moment you are standing a
// server up next to a phone; --no-qr for a log nobody is looking at.
var showQr = !Flag("--no-qr");

// Colour is what fixes the polarity: a QR must be dark on light, and a terminal that draws text
// light-on-dark would otherwise hand a scanner the negative. Honour NO_COLOR, and drop it when the
// output is redirected, where escape codes are just litter in a file.
var useColour = !Flag("--no-colour")
    && Environment.GetEnvironmentVariable("NO_COLOR") is null
    && !Console.IsOutputRedirected;

var dataDir = Arg("--data") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Banter", "nodestar");
Directory.CreateDirectory(dataDir);

var concordium = Arg("--network") ?? "banter";

// Every port this process binds is named here, and none is left to Nodestar's defaults.
//
// Inheriting them meant a Banter node and a Nodestar node could not run side by side — they chose
// the same web front and the same overlay port, so whichever started second failed to bind. These
// sit in Banter's own block beside the socket server on 7770, which keeps a whole deployment
// findable in one range and out of the way of the framework it is built on.
//
// The site port is the site's own front door, and not the node's beacon port: a vessel accepted
// there reaches the node, which has no Shrine behind it (CupriNodestar#2). Clients dial this one.
var sitePort = Port("--site-port", 7771);      // vessels, for desktop clients
var listenPort = Port("--listen-port", 7772);  // the node's own overlay beacon
var webRtcPort = Port("--webrtc-port", 7773);  // the browser on-ramp (UDP)
var webPort = Port("--web-port", 7774);        // the clearnet front, and the link.json it serves

// The browser client, served by THIS node. One server: whatever carries the mesh also hands out
// the page that dials it, so there is no second origin to keep in step and no question of which
// of them is the real one.
//
// This used to be a file. The node wrote its link into the web head's wwwroot and a separate dev
// server served it, which is two servers and a link with a lifetime sitting in a file — stale the
// moment the node that wrote it went away, and failing then in a way that reads as the transport
// being broken. Nodestar serves the bundle under /_nodestar/app instead, stamping a <base> into
// the markup so it is mount-point-agnostic, and publishes the live link beside it at
// /_nodestar/app/intonation.json. That endpoint is what removes the signalling server: the page
// holds this node's remote description before it opens a socket.
//
// Defaults to a "client" directory beside the executable, so a published server that ships the
// head needs no argument at all.
var clientRoot = Arg("--client") ?? Path.Combine(AppContext.BaseDirectory, "client");
var clientAssets = new DirectoryClientAssets(clientRoot);
var adminPassword = Environment.GetEnvironmentVariable("BANTER_ADMIN_PASSWORD") ?? "admin";

var storage = BanterStorageOptions.Parse("sqlite", $"Data Source={Path.Combine(dataDir, "banter.db")}");
var database = new BanterDatabase(storage);
await database.InitializeAsync();

var accounts = new DbAccountStore(database);
if (!await accounts.ExistsAsync("admin"))
{
    await accounts.CreateUserAsync("admin", adminPassword, isAgent: false, isAdmin: true);
    Console.WriteLine("Created the 'admin' account.");
}

if (adminPassword == "admin")
{
    Console.WriteLine();
    Console.WriteLine("  *** WARNING: the 'admin' account is using the default password. ***");
    Console.WriteLine();
}

var fileStore = new FileStore(database, new FileStoreOptions { DataDirectory = dataDir });

var builder = NodestarApplication.CreateBuilder(args);
builder.Node.Concordium = concordium;
builder.Node.DataDirectory = Path.Combine(dataDir, "mesh");
builder.Node.SiteName = "Banter";
builder.Node.ListenPort = listenPort;
builder.Node.WebRtcPort = webRtcPort;
builder.Node.WebPort = webPort;

// A Pilgrim pins the SITE's Signet, so a node that does not put one in its link cannot be visited
// at all — the link would describe a node and nothing it hosts.
builder.Node.AdvertiseSiteInLink = true;

// The browser on-ramp. Without this the WebRTC endpoint never reaches the link, and the web head
// has nothing to dial: it is the transport, not merely a flag.
builder.UseWebRtc();

// Separate from UseWebRtc on purpose, and worth keeping separate here too: accepting browser
// DataChannels and deciding what runs in the browser are different decisions. A node with no
// client built beside it is still a perfectly good node for the desktop and phone heads.
if (clientAssets.HasContent)
{
    builder.ServeClient(clientAssets.Get);
}

// The listener has to exist before the server, because the site is registered while the node is
// being built and the node is running before there is anything to hand it to.
var listener = builder.Site.ServeBanter(new Uri($"cupri://{concordium}/banter"));

// Announced from OnStarted, not after Build: the site address does not exist until the node is
// online, and printing it earlier says "Banter is on the site at " with nothing after it.
ShrineVesselHost? host = null;

builder.OnStarted((app, cancellationToken) =>
{
    // Started here rather than after Build for the same reason the address is printed here: the
    // site does not exist to be served until the node is online.
    host = new ShrineVesselHost(app, new IPEndPoint(IPAddress.Any, sitePort));
    host.Start(cancellationToken);

    Console.WriteLine($"Banter is on the site at {app.SiteAddress}");
    Console.WriteLine(
        $"Ports: vessels {host.LocalEndPoint.Port}, overlay {listenPort}, " +
        $"WebRTC {webRtcPort}/udp, web {webPort}.");

    // The link is what a browser needs, and the only thing it needs: it carries the site's Signet,
    // the network, and the WebRTC credentials the browser writes the node's answer from.
    var links = new NodestarLinkProvider(app.Node, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
    var link = links.Current().Link;
    Console.WriteLine();

    // The code first and the text second, because they are for different readers: a phone points
    // its camera at the code, and a browser on this machine takes the text. Printing ~380
    // characters and expecting anyone to retype them onto a phone was the state of the art here
    // until now.
    //
    // Only when the window can hold it. A code wider than the terminal wraps, and a wrapped QR is
    // not a smaller QR — it is noise that looks like a QR, which wastes more of somebody's time
    // than not printing one would have.
    var columns = TerminalQr.Columns(link);
    var width = TerminalWidth();
    if (!showQr)
    {
        // Asked not to. Nothing to say about it.
    }
    else if (width is { } available && available < columns)
    {
        Console.WriteLine(
            $"The QR needs {columns} columns and this window has {available}. Widen it and " +
            "restart for a code a phone can scan, or use the link below.");
    }
    else
    {
        Console.WriteLine("Scan this with a phone, or paste the link below:");
        Console.WriteLine();
        Console.Write(TerminalQr.Render(link, colour: useColour));
    }

    Console.WriteLine();
    Console.WriteLine(link);
    Console.WriteLine();

    if (clientAssets.HasContent)
    {
        Console.WriteLine($"The web client is at http://localhost:{webPort}/_nodestar/app");
        Console.WriteLine($"  from {clientAssets.Root}");
    }
    else
    {
        // Said out loud rather than left as a 404 later. A node with no client is a legitimate
        // deployment, but so is "I meant to build one and the path is wrong", and those two look
        // identical from a browser.
        Console.WriteLine($"No web client at {clientAssets.Root}.");
        Console.WriteLine("  Publish Banter.App.Web there, or pass --client <wwwroot>.");
    }

    Console.WriteLine();

    Console.WriteLine("Press Ctrl+C to stop.");
    return Task.CompletedTask;
});

var nodestar = builder.Build();

await using var server = new BanterServer(
    new PreparedListenerTransport(listener),
    accounts,
    new DbServerStore(database),
    fileStore,
    tasks: new TaskStore(database),
    // The same identity model as the TCP server: this is the mesh's front door, not a different
    // idea of who an agent is.
    identities: new AgentIdentityStore(database),
    accountAdmin: accounts);

await server.StartAsync(listener.LocalEndpoint);

var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

try
{
    await nodestar.RunAsync(stopping.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C.
}

if (host is not null)
{
    await host.DisposeAsync();
}

await listener.DisposeAsync();
await nodestar.DisposeAsync();
return 0;

int Port(string name, int fallback) =>
    int.TryParse(Arg(name), out var value) ? value : fallback;

/// <summary>The window's width, or null when there is no window to ask — a redirected stream or a
/// container, where Console.WindowWidth throws or answers zero rather than a width.</summary>
int? TerminalWidth()
{
    if (Console.IsOutputRedirected)
    {
        return null;
    }

    try
    {
        return Console.WindowWidth > 0 ? Console.WindowWidth : null;
    }
    catch (IOException)
    {
        return null;
    }
}

bool Flag(string name) => Array.IndexOf(args, name) >= 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
