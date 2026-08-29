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

var dataDir = Arg("--data") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Banter", "nodestar");
Directory.CreateDirectory(dataDir);

var concordium = Arg("--network") ?? "banter";

// The site's own front door, and not the node's beacon port: a vessel accepted there reaches the
// node, which has no Shrine behind it (CupriNodestar#2). Clients dial this one.
var sitePort = int.TryParse(Arg("--site-port"), out var parsed) ? parsed : 7411;

// Where to drop this node's link so a browser client can seed itself instead of being pasted
// into. The web head fetches it from its own origin, which is why this is a file rather than an
// endpoint: in development the client is served by its own dev server, not by this node.
var seedFile = Arg("--seed-file");

// The seed file exists only while this node does, which takes deleting it at both ends.
//
// A seed outliving its node is the trap: it names a node that is gone, or names this one with a
// dead process's ICE credentials, and a client that reads it hangs partway through a handshake
// with nothing to talk to. That reads as the client being broken. Far better to leave no link at
// all — the client then knows to wait, and says so.
ClearSeed();

void ClearSeed()
{
    if (seedFile is null)
    {
        return;
    }

    try
    {
        File.Delete(seedFile);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"warning: could not clear the seed file: {ex.Message}");
    }
}
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

// A Pilgrim pins the SITE's Signet, so a node that does not put one in its link cannot be visited
// at all — the link would describe a node and nothing it hosts.
builder.Node.AdvertiseSiteInLink = true;

// The browser on-ramp. Without this the WebRTC endpoint never reaches the link, and the web head
// has nothing to dial: it is the transport, not merely a flag.
builder.UseWebRtc();

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
    Console.WriteLine($"Desktop clients dial this node on port {host.LocalEndPoint.Port}.");

    // The link is what a browser needs, and the only thing it needs: it carries the site's Signet,
    // the network, and the WebRTC credentials the browser writes the node's answer from.
    var links = new NodestarLinkProvider(app.Node, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
    Console.WriteLine();
    Console.WriteLine("Paste this into the web client's Server field:");
    Console.WriteLine(links.Current().Link);
    Console.WriteLine();

    if (seedFile is not null)
    {
        // Rewritten on a timer rather than written once: a link has a lifetime and rotates, so a
        // seed file written at startup is stale by the time a long debugging session gets back to
        // it — and a stale link fails in a way that looks like the transport being broken.
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(seedFile))!);
                    await File.WriteAllTextAsync(
                        seedFile,
                        System.Text.Json.JsonSerializer.Serialize(new { link = links.Current().Link }),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A seed nobody can write is a convenience lost, not a node that should stop.
                    Console.Error.WriteLine($"warning: could not write the seed file: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, cancellationToken);

        Console.WriteLine($"Seeding the web client from {seedFile}.");
        Console.WriteLine();
    }

    Console.WriteLine("Press Ctrl+C to stop.");
    return Task.CompletedTask;
});

var nodestar = builder.Build();

await using var server = new BanterServer(
    new PreparedListenerTransport(listener),
    accounts,
    new DbServerStore(database),
    fileStore,
    tasks: new TaskStore(database));

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

// The node is down, so its link is worthless. Taken away rather than left to mislead whoever
// loads the page next. A hard kill skips this, which is what the delete at startup is for.
ClearSeed();
return 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
