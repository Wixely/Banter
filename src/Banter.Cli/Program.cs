using Banter.Client.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;

var server = Arg("--server") ?? "tcp://127.0.0.1:7770";
var user = Arg("--user");
var pass = Arg("--pass");
if (user is null || pass is null)
{
    Console.Error.WriteLine("usage: banter-cli [--server tcp://host:port] --user <name> --pass <secret>");
    return 1;
}

BanterClient client;
try
{
    client = await BanterClient.ConnectAsync(new TcpBanterTransport(), new Uri(server), user, pass);
}
catch (Exception ex) when (ex is BanterClientException or IOException or System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine($"Connect failed: {ex.Message}");
    return 1;
}

await using var _ = client;
var currentRoom = (string?)null;
var done = new TaskCompletionSource();

client.MessageReceived += m => Print($"[{m.Room}] <{m.Sender}> {m.Text}");
client.PrivateMessageReceived += m => Print($"[pm] <{m.Sender}> {m.Text}");
client.MemberJoined += j => Print($"[{j.Room}] * {j.Nick} joined");
client.MemberParted += p => Print($"[{p.Room}] * {p.Nick} left{(p.Reason is null ? "" : $" ({p.Reason})")}");
client.TopicChanged += t => Print($"[{t.Room}] * topic: {t.Topic}");
client.Disconnected += () => Print("* connection lost - reconnecting in the background (/quit to exit)");
client.Reconnecting += attempt => Print($"* reconnect attempt {attempt}...");
client.Reconnected += () => Print("* reconnected");

Print($"connected to {server} as {client.Nick} -- /join #room to start, /help for commands");

var input = Task.Run(async () =>
{
    while (!done.Task.IsCompleted)
    {
        var line = Console.ReadLine();
        if (line is null)
        {
            break;
        }

        try
        {
            // Trim whitespace and any BOM a piping shell may prepend.
            if (!await HandleAsync(line.Trim().Trim((char)0xFEFF)))
            {
                break;
            }
        }
        catch (BanterErrorException ex)
        {
            Print($"! {ex.Message}");
        }
        catch (BanterClientException ex)
        {
            Print($"! {ex.Message}");
        }
    }

    done.TrySetResult();
});

await done.Task;
return 0;

async Task<bool> HandleAsync(string line)
{
    if (line.Length == 0)
    {
        return true;
    }

    if (!line.StartsWith('/'))
    {
        if (currentRoom is null)
        {
            Print("! join a room first: /join #room");
            return true;
        }

        await client.SendMessageAsync(currentRoom, line);
        return true;
    }

    var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
    var argument = parts.Length > 1 ? parts[1] : null;
    switch (parts[0].ToLowerInvariant())
    {
        case "/join" when argument is not null:
            await client.JoinAsync(argument);
            currentRoom = argument;
            Print($"* now talking in {argument}");
            var history = await client.GetHistoryAsync(argument, limit: 20);
            foreach (var m in history.Messages)
            {
                Print($"[{m.Room}] <{m.Sender}> {m.Text}   ({DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp):HH:mm})");
            }

            return true;
        case "/part":
            var target = argument ?? currentRoom;
            if (target is not null)
            {
                await client.PartAsync(target);
                if (target == currentRoom)
                {
                    currentRoom = null;
                }
            }

            return true;
        case "/topic" when currentRoom is not null && argument is not null:
            await client.SetTopicAsync(currentRoom, argument);
            return true;
        case "/rooms":
            var rooms = await client.ListRoomsAsync();
            foreach (var r in rooms.Rooms)
            {
                Print($"  {r.Name} ({r.MemberCount}){(r.Topic is null ? "" : $" -- {r.Topic}")}");
            }

            return true;
        case "/members" when (argument ?? currentRoom) is { } room:
            var members = await client.GetMembersAsync(room);
            foreach (var m in members.Members)
            {
                Print($"  {m.Nick}{(m.IsAgent ? " [agent]" : "")}");
            }

            return true;
        case "/ping":
            Print($"* pong in {(await client.PingAsync()).TotalMilliseconds:F0} ms");
            return true;
        case "/quit":
            return false;
        case "/help":
            Print("commands: /join #room | /part [#room] | /topic <text> | /rooms | /members [#room] | /ping | /quit -- anything else is said in the current room");
            return true;
        default:
            Print("! unknown or incomplete command -- /help");
            return true;
    }
}

static void Print(string text) => Console.WriteLine(text);

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
