using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Banter.Server.Tools;

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
    Console.Error.WriteLine("usage: banter-server [--endpoint tcp://host:port | ws://host:port] [--db sqlite|postgres] [--connection <connection-string>]");
    Console.Error.WriteLine("                     [--admin-password <secret>] [--seed-users name:password,name:password]");
    return 1;
}

var database = new BanterDatabase(storage);
await database.InitializeAsync();
var accounts = new DbAccountStore(database);

// The admin account always exists. It is the account the oversight rule in PLAN §8a depends on:
// admins are added to every room an agent opens, so a deployment with no admin has agents
// holding conversations nobody is watching.
// _FILE first: reading a secret from a mounted file keeps it out of `docker inspect`, the
// process environment and shell history, which an environment variable cannot manage.
var adminPassword = ReadSecretFile(Environment.GetEnvironmentVariable("BANTER_ADMIN_PASSWORD_FILE"))
    ?? Arg("--admin-password")
    ?? Environment.GetEnvironmentVariable("BANTER_ADMIN_PASSWORD")
    ?? "admin";

if (!await accounts.ExistsAsync("admin"))
{
    await accounts.CreateUserAsync("admin", adminPassword, isAgent: false, isAdmin: true);
    Console.WriteLine("Created the 'admin' account.");
}
else
{
    // An upgrade from before admins existed, or an operator who cleared the flag by hand.
    await accounts.SetAdminAsync("admin", isAdmin: true);
}

if (adminPassword == "admin")
{
    // Loud, not a footnote: a default admin password on a reachable server is the whole
    // deployment's security, and the fix is one environment variable.
    Console.WriteLine();
    Console.WriteLine("  *** WARNING: the 'admin' account is using the default password. ***");
    Console.WriteLine("  Set BANTER_ADMIN_PASSWORD (or --admin-password) before exposing this server.");
    Console.WriteLine();
}

// Accounts are only ever created because somebody asked for them. An empty database used to
// grow alice and bob with the password "banter" on its own, which is a fine thing on a laptop
// and an indefensible one anywhere else: the accounts arrive unannounced, the password is
// public knowledge, and the deployment that most needs them absent is the one least likely to
// look. So it is configuration now — --seed-users, or BANTER_SEED_USERS in a container.
//
// Only humans, and only ones that do not already exist. Agents are never seeded: an agent is
// created on the admin UI's agents page (or /agent add in Banter.Cli), enrols with the one-time
// code that hands back, and authenticates with the key it made — the server never holds an
// agent password because the agent never has one.
var seedSpec = ReadSecretFile(Environment.GetEnvironmentVariable("BANTER_SEED_USERS_FILE"))
    ?? Arg("--seed-users")
    ?? Environment.GetEnvironmentVariable("BANTER_SEED_USERS");

if (seedSpec is { Length: > 0 })
{
    foreach (var entry in seedSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        // name:password. A colon rather than '=' so a password containing '=' (base64 padding,
        // most obviously) needs no escaping; the split is on the first colon only, so a password
        // may contain them freely.
        var colon = entry.IndexOf(':');
        if (colon <= 0 || colon == entry.Length - 1)
        {
            Console.Error.WriteLine($"error: --seed-users entry '{entry}' is not name:password.");
            return 2;
        }

        var name = entry[..colon].Trim();
        var secret = entry[(colon + 1)..];

        if (await accounts.ExistsAsync(name))
        {
            // Not an error, and not a password reset either: re-running a container with the
            // same configuration must not silently change a password somebody has since set.
            Console.WriteLine($"Account '{name}' already exists - left alone.");
            continue;
        }

        await accounts.CreateUserAsync(name, secret);
        Console.WriteLine($"Created the '{name}' account.");
    }
}
else if (await accounts.CountAsync() <= 1)
{
    // Said out loud, because an empty server that silently accepts no one looks broken. The
    // admin account exists, so this is a working server rather than a stuck one.
    Console.WriteLine("No user accounts besides 'admin'. Add them as admin on the users page,");
    Console.WriteLine("or start with --seed-users alice:secret,bob:secret (BANTER_SEED_USERS in a container).");
}

var dataDir = Arg("--data") ?? Environment.GetEnvironmentVariable("BANTER_DATA") ?? "banter-data";
var fileStore = new FileStore(database, new FileStoreOptions { DataDirectory = dataDir });

// Tools run here, on the server, never on the agent (PLAN §8). The upstream credentials live
// with this process; agents only ever ask it to act.
var mcpConfig = Arg("--mcp") ?? Environment.GetEnvironmentVariable("BANTER_MCP_CONFIG") ?? "mcp.json";
var mcpOptions = McpConfigFile.Load(mcpConfig);
await using var toolBroker = new McpToolBroker(mcpOptions, new ToolGrantStore(database));
if (mcpOptions.Upstreams.Count > 0)
{
    await toolBroker.StartAsync();
    Console.WriteLine(
        $"MCP: {toolBroker.Upstreams.Count}/{mcpOptions.Upstreams.Count} upstream(s) connected, " +
        $"{toolBroker.AllTools().Count} tool(s) available to grant.");
}

// The scheme picks the transport, exactly as it does on the client. ws:// is what a browser
// needs, since script cannot open a socket (PLAN §2.5).
var transport = BanterTransports.Server(endpoint);

await using var server = new BanterServer(
    transport, accounts, new DbServerStore(database), fileStore,
    guardrails: null,
    tasks: new TaskStore(database),
    tools: toolBroker,
    // Agent identities: an admin creates one and is handed a single-use enrolment code, the
    // machine that will run it redeems that code for a key it generates itself, and removal takes
    // effect on the next thing the agent tries. The server is the authority, so none of this needs
    // a credential in the wild that has to be waited out.
    identities: new AgentIdentityStore(database),
    // The users page's authority: humans created and reset by an admin at run time. Nothing else
    // creates an account except the admin account and an explicit --seed-users.
    accountAdmin: accounts);
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

static string? ReadSecretFile(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    try
    {
        // Trailing newlines are what an editor or `echo` leaves behind, and a password that
        // silently includes one is a very confusing failure.
        var value = File.ReadAllText(path).Trim();
        return value.Length > 0 ? value : null;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Refusing to start would be worse: the server still comes up, with the default and its
        // warning, rather than a container that crash-loops over a mount typo.
        Console.Error.WriteLine($"warning: could not read {path}: {ex.Message}");
        return null;
    }
}

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
