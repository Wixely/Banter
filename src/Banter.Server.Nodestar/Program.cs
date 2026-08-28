using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Banter.Transport.Shrine;
using CupriNet.Nodestar;

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

// The listener has to exist before the server, because the site is registered while the node is
// being built and the node is running before there is anything to hand it to.
var listener = builder.Site.ServeBanter(new Uri($"cupri://{concordium}/banter"));

// Announced from OnStarted, not after Build: the site address does not exist until the node is
// online, and printing it earlier says "Banter is on the site at " with nothing after it.
builder.OnStarted((app, _) =>
{
    Console.WriteLine($"Banter is on the site at {app.SiteAddress}");
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

await listener.DisposeAsync();
await nodestar.DisposeAsync();
return 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
