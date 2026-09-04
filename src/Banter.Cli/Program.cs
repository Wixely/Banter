using Banter.Client.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;

var server = Arg("--server") ?? "tcp://127.0.0.1:7770";
var user = Arg("--user");
var pass = Arg("--pass");
if (user is null || pass is null)
{
    Console.Error.WriteLine($"usage: banter-cli [--server <{string.Join("|", BanterTransports.Schemes)}>://host:port] --user <name> --pass <secret>");
    return 1;
}

BanterClient client;
try
{
    var endpoint = new Uri(server);
    client = await BanterClient.ConnectAsync(BanterTransports.Client(endpoint), endpoint, user, pass);
}
catch (Exception ex) when (ex is BanterClientException or IOException or System.Net.Sockets.SocketException or ArgumentException)
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
        case "/msg" when argument is not null && argument.Split(' ', 2) is [var to, var text]:
            await client.SendPrivateMessageAsync(to, text);
            Print($"[pm -> {to}] {text}");
            return true;
        case "/files" when (argument ?? currentRoom) is { } filesRoom:
            var files = await client.ListFilesAsync(filesRoom);
            foreach (var f in files.Files)
            {
                Print($"  {f.FileId}  {f.Name} ({f.Size} bytes, {f.MimeType}) by {f.Uploader}");
            }

            return true;
        case "/upload" when currentRoom is not null && argument is not null:
            var path = argument.Trim('"');
            var uploaded = await client.UploadFileAsync(
                currentRoom, Path.GetFileName(path), await File.ReadAllBytesAsync(path), "application/octet-stream");
            Print($"* uploaded {uploaded.Name} as {uploaded.FileId}");
            return true;
        case "/download" when argument is not null && argument.Split(' ', 2) is [var dlId, .. var rest]:
            var bytes = await client.DownloadFileAsync(dlId);
            var savePath = rest is [var dest] ? dest.Trim('"') : (await client.GetFileInfoAsync(dlId)).Name;
            await File.WriteAllBytesAsync(savePath, bytes);
            Print($"* downloaded {bytes.Length} bytes to {savePath}");
            return true;
        // Agent identities. Admin-only on the server, so a non-admin gets NOT_ADMIN back rather
        // than a client-side guess about who they are.
        case "/agent" when argument is not null:
        {
            var words = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (words)
            {
                case ["list"]:
                {
                    var agents = await client.ListAgentsAsync();
                    if (agents.Count == 0)
                    {
                        Print("* no agent identities yet -- /agent add <nick> [rooms] [skills] [local|frontier] [public|internal|sensitive]");
                        break;
                    }

                    foreach (var a in agents)
                    {
                        // The fingerprint is what tells one machine from another, so it is worth
                        // showing beside the name rather than hiding behind a detail view.
                        var state = a.Enrolled ? $"key {a.KeyFingerprint}"
                            : a.EnrolmentPending ? "awaiting enrolment"
                            : "no key, no code -- reissue to give it one";
                        Print($"  {a.Nick,-14} {a.Locality,-8} {a.Clearance,-9} {string.Join(",", a.Rooms),-20} {string.Join(",", a.Skills),-18} {state}");
                    }

                    break;
                }

                case ["add", var nick, .. var rest]:
                {
                    string[] joinRooms = rest.Length > 0 ? rest[0].Split(',') : [currentRoom!];
                    string[] joinSkills = rest.Length > 1 ? rest[1].Split(',') : ["chat"];
                    var locality = rest.Length > 2 && rest[2].Equals("frontier", StringComparison.OrdinalIgnoreCase)
                        ? AgentLocality.Frontier
                        : AgentLocality.Local;
                    var clearance = rest.Length > 3 && Enum.TryParse<DataSensitivity>(rest[3], true, out var c)
                        ? c
                        : DataSensitivity.Sensitive;

                    var created = await client.CreateAgentAsync(nick!, joinRooms, joinSkills, locality, clearance);
                    Print($"* created '{created.Nick}'. Paste this into the machine that will run it, within the hour:");
                    Print("");
                    Print($"    {created.Code}");
                    Print("");
                    Print("  It works once. Nothing else needs to be copied -- the agent makes its own key.");
                    break;
                }

                case ["reissue", var nick]:
                {
                    var reissued = await client.ReissueAgentAsync(nick!);
                    Print($"* '{reissued.Nick}' now has no key, and this code will give it one:");
                    Print("");
                    Print($"    {reissued.Code}");
                    Print("");
                    Print("  The machine it was on before can no longer connect.");
                    break;
                }

                case ["remove", var nick]:
                    await client.DeleteAgentAsync(nick!);
                    Print($"* removed '{nick}'. Its key stops working immediately.");
                    break;

                case ["rooms", var nick, var inRooms]:
                    await client.UpdateAgentAsync(nick!, rooms: inRooms!.Split(','));
                    Print($"* '{nick}' is now in {inRooms}");
                    break;

                case ["skills", var nick, var hasSkills]:
                    await client.UpdateAgentAsync(nick!, skills: hasSkills!.Split(','));
                    Print($"* '{nick}' now does {hasSkills}");
                    break;

                case ["clearance", var nick, var level] when Enum.TryParse<DataSensitivity>(level, true, out var parsed):
                    await client.UpdateAgentAsync(nick!, clearance: parsed);
                    Print($"* '{nick}' is cleared for {parsed.ToString().ToLowerInvariant()}");
                    break;

                default:
                    Print("! /agent list | add <nick> [rooms] [skills] [local|frontier] [public|internal|sensitive]");
                    Print("!        | rooms <nick> <a,b> | skills <nick> <a,b> | clearance <nick> <level>");
                    Print("!        | reissue <nick> | remove <nick>");
                    break;
            }

            return true;
        }

        // User accounts: the humans' mirror of /agent. Admin-only on the server except /passwd,
        // which is anyone changing their own.
        case "/user" when argument is not null:
        {
            var words = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (words)
            {
                case ["list"]:
                {
                    var users = await client.ListUsersAsync();
                    foreach (var u in users)
                    {
                        Print($"  {u.Username,-20} {(u.IsAdmin ? "admin" : "member")}");
                    }

                    break;
                }

                case ["add", var name, .. var rest]:
                {
                    var asAdmin = rest is ["admin"];
                    var created = await client.CreateUserAsync(name!, asAdmin);
                    Print($"* created '{created.Username}'{(asAdmin ? " as an admin" : "")}. Hand them this, once:");
                    Print("");
                    Print($"    {created.Password}");
                    Print("");
                    Print("  They should change it with /passwd the first time they sign in.");
                    break;
                }

                case ["reset", var name]:
                {
                    var reset = await client.ResetUserPasswordAsync(name!);
                    Print($"* '{reset.Username}' has a new temporary password; the old one is dead:");
                    Print("");
                    Print($"    {reset.Password}");
                    Print("");
                    break;
                }

                case ["admin", var name]:
                    await client.SetUserAdminAsync(name!, true);
                    Print($"* '{name}' is now an admin");
                    break;

                case ["member", var name]:
                    await client.SetUserAdminAsync(name!, false);
                    Print($"* '{name}' is now an ordinary member");
                    break;

                case ["remove", var name]:
                    await client.DeleteUserAsync(name!);
                    Print($"* removed '{name}'. Their password stops working immediately.");
                    break;

                default:
                    Print("! /user list | add <name> [admin] | reset <name> | admin <name> | member <name> | remove <name>");
                    break;
            }

            return true;
        }

        case "/passwd" when argument?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var oldPass, var newPass]:
            await client.ChangeMyPasswordAsync(oldPass, newPass);
            Print("* password changed");
            return true;

        case "/passwd":
            Print("! /passwd <current> <new>");
            return true;

        case "/ping":
            Print($"* pong in {(await client.PingAsync()).TotalMilliseconds:F0} ms");
            return true;
        case "/quit":
            return false;
        case "/help":
            Print("commands: /join #room | /part [#room] | /topic <text> | /msg <nick> <text> | /rooms | /members [#room] | /files [#room] | /upload <path> | /download <id> [path] | /agent ... | /user ... | /passwd <current> <new> | /ping | /quit -- anything else is said in the current room");
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
