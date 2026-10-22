using System.Text.Json;
using System.Threading.Channels;
using Banter.Protocol;

namespace Banter.Server;

/// <summary>
/// Every message in and out of the server, one JSON line each, for working out afterwards what
/// happened: who said what in which room, which tool an agent reached for and what came back,
/// which delegator was elected when, in the order it all arrived.
///
/// <para>Opt-in, by <c>--trace &lt;file&gt;</c>. It records message text and tool results, which is
/// the point of it and also why it is not on by default: a trace is the conversation, and belongs
/// wherever the conversation does. Arguments to tools are recorded only by length, as the room's
/// own audit line already decided - they routinely carry file contents and credentials.</para>
///
/// <para>Written off the session's thread through a channel, so a slow disk cannot slow a room,
/// and flushed on every line, so a trace survives whatever the server does next.</para>
/// </summary>
public sealed class WireTrace : IAsyncDisposable
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly StreamWriter _file;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private WireTrace(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _file = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _writer = Task.Run(PumpAsync);
    }

    /// <summary>Where the lines go. Appended to, so a restart continues the same file.</summary>
    public string Path { get; }

    public static WireTrace Open(string path) => new(path);

    /// <summary>One message. <paramref name="direction"/> is "in" (peer → server) or "out".</summary>
    public void Record(string direction, string nick, object payload, int bytes, string? replyTo = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["t"] = DateTimeOffset.UtcNow.ToString("O"),
            ["dir"] = direction,
            ["nick"] = nick.Length > 0 ? nick : null,
            ["type"] = payload.GetType().Name.Replace("Payload", "", StringComparison.Ordinal),
            ["bytes"] = bytes,
            ["replyTo"] = replyTo,
        };

        // What is worth reading, per kind. Everything else is the type and its size, which is
        // enough to see the shape of a session without a dictionary of every payload.
        switch (payload)
        {
            case MsgPayload m:
                entry["room"] = m.Room; entry["sender"] = m.Sender; entry["id"] = m.MessageId;
                entry["text"] = m.Text; entry["file"] = m.FileId; entry["to"] = m.To; entry["replyToMessage"] = m.ReplyTo;
                break;
            case MsgStreamStartPayload s:
                entry["room"] = s.Room; entry["sender"] = s.Sender; entry["stream"] = s.StreamId;
                break;
            case MsgStreamDeltaPayload d:
                entry["stream"] = d.StreamId; entry["delta"] = d.Delta;
                break;
            case MsgStreamEndPayload e:
                entry["stream"] = e.StreamId; entry["id"] = e.MessageId; entry["text"] = e.FinalText;
                break;
            case ToolCallPayload c:
                entry["tool"] = c.Name; entry["room"] = c.Room; entry["argumentBytes"] = c.Arguments.Length;
                break;
            case ToolResultPayload r:
                entry["tool"] = r.Name; entry["error"] = r.IsError; entry["content"] = r.Content;
                break;
            case AskPayload a:
                entry["room"] = a.Room; entry["ask"] = a.AskId; entry["questions"] = a.Questions.Select(q => q.Text).ToList();
                break;
            case AnswerPayload an:
                entry["ask"] = an.AskId; entry["responder"] = an.Responder;
                break;
            case JoinPayload j:
                entry["room"] = j.Room; entry["who"] = j.Nick;
                break;
            case PartPayload p:
                entry["room"] = p.Room; entry["who"] = p.Nick; entry["reason"] = p.Reason;
                break;
            case RoomDelegatorPayload rd:
                entry["room"] = rd.Room; entry["delegator"] = rd.Nick; entry["reason"] = rd.Reason;
                break;
            case AgentAnnouncePayload ann:
                entry["agent"] = ann.Nick; entry["locality"] = ann.Locality.ToString(); entry["clearance"] = ann.Clearance.ToString();
                entry["skills"] = ann.Skills; entry["sdk"] = ann.Sdk;
                break;
            case ErrorPayload err:
                entry["code"] = err.Code; entry["message"] = err.Message;
                break;
            case AgentMovePayload mv:
                entry["room"] = mv.Room; entry["who"] = mv.Nick;
                break;
            case RoomCreatePayload rc:
                entry["room"] = rc.Room; entry["parent"] = rc.ParentRoom;
                break;
        }

        _lines.Writer.TryWrite(JsonSerializer.Serialize(entry, Json));
    }

    /// <summary>The last <paramref name="count"/> lines written so far, oldest first.</summary>
    public async Task<IReadOnlyList<string>> TailAsync(int count)
    {
        // Through a reader of our own: the writer's handle is append-only.
        await using var read = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(read);
        var ring = new Queue<string>(count + 1);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            ring.Enqueue(line);
            if (ring.Count > count)
            {
                ring.Dequeue();
            }
        }

        return [.. ring];
    }

    private async Task PumpAsync()
    {
        await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _file.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        await _file.DisposeAsync().ConfigureAwait(false);
    }
}
