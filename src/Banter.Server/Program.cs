using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;

var endpoint = new Uri(Arg("--endpoint") ?? "tcp://127.0.0.1:7770");
BanterStorageOptions storage;
try
{
    storage = BanterStorageOptions.Parse(
        Arg("--db") ?? Environment.GetEnvironmentVariable("BANTER_DB"),
        Arg("--connection") ?? Environment.GetEnvironmentVariable("BANTER_CONNECTION"));
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("usage: banter-server [--endpoint tcp://host:port] [--db sqlite|postgres] [--connection <connection-string>]");
    return 1;
}

var database = new BanterDatabase(storage);
await database.InitializeAsync();
var accounts = new DbAccountStore(database);

if (await accounts.CountAsync() == 0)
{
    // First run against an empty database: seed development users so the suite is usable
    // immediately. Real deployments create accounts via admin tooling (Banter.Cli, later).
    // Two agent accounts, because one agent cannot demonstrate delegation: election, hand-off
    // and the local-vs-frontier rules all need a room with more than one candidate in it.
    Console.WriteLine("No accounts found - seeding development users alice/bob and agents dagger/scout (password: banter).");
    await accounts.CreateUserAsync("alice", "banter");
    await accounts.CreateUserAsync("bob", "banter");
    await accounts.CreateUserAsync("dagger", "banter", isAgent: true);
    await accounts.CreateUserAsync("scout", "banter", isAgent: true);
}

var dataDir = Arg("--data") ?? Environment.GetEnvironmentVariable("BANTER_DATA") ?? "banter-data";
var fileStore = new FileStore(database, new FileStoreOptions { DataDirectory = dataDir });

await using var server = new BanterServer(new TcpBanterTransport(), accounts, new DbServerStore(database), fileStore);
await server.StartAsync(endpoint);
Console.WriteLine($"Banter.Server listening on {server.Endpoint} ({storage.Provider} storage)");
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
return 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
