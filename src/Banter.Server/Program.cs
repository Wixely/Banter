using Banter.Core;
using Banter.Protocol.Transport;
using Banter.Server;

var endpoint = new Uri(args.Length > 0 ? args[0] : "tcp://127.0.0.1:7770");

// Development accounts until the SQLite store lands (Phase 1 follow-up).
var accounts = new InMemoryAccountStore()
    .AddUser("alice", "banter")
    .AddUser("bob", "banter")
    .AddUser("dagger", "banter", isAgent: true);

await using var server = new BanterServer(new TcpBanterTransport(), accounts);
await server.StartAsync(endpoint);
Console.WriteLine($"Banter.Server listening on {server.Endpoint}");
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
