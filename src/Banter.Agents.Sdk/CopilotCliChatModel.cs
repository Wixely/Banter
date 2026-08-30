using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Banter.Agents.Sdk;

/// <summary>How a Copilot-backed agent is configured.</summary>
public sealed record CopilotCliOptions
{
    /// <summary>The executable. On PATH by default; a full path works too.</summary>
    public string Executable { get; init; } = "copilot";

    /// <summary>Which model Copilot should use. Empty leaves it to Copilot's own default.</summary>
    public string Model { get; init; } = "";

    /// <summary>
    /// The directory the CLI runs in, and therefore the only place it can reach files without
    /// asking. Defaults to a directory of its own under the temp path rather than the caller's —
    /// running a chat agent in a source tree would give the room's participants a foothold in it.
    /// </summary>
    public string WorkingDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "banter-copilot");

    /// <summary>
    /// Whether Copilot may use its own tools — shell commands, file edits, the GitHub MCP server.
    ///
    /// <para><b>Off, and it should stay off.</b> This agent's prompts are whatever people and other
    /// agents type into a room, so anything it can do, a room can talk it into doing. Banter runs
    /// tools server-side under per-agent grants and announces every call in the room (PLAN §8c);
    /// a backend with its own tools would bypass all of that and leave no audit trail.</para>
    /// </summary>
    public bool AllowCopilotTools { get; init; }

    /// <summary>How long one reply may take before the process is killed.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// An <see cref="IChatModel"/> backed by the GitHub Copilot CLI, driven as a subprocess: one run
/// per reply, prompt in, JSONL out.
///
/// <para><b>Stateless per turn, on purpose.</b> Copilot can resume a session
/// (<c>--resume &lt;id&gt;</c>), and it is tempting to keep one per room. But the agent above
/// already holds per-room context and trims it, and its whole guarantee is that the same agent in
/// two rooms cannot leak one into the other. A second, invisible conversation state living in
/// Copilot's session store would be a way for exactly that to happen. So each turn renders the
/// room's context into one prompt and nothing is carried in the CLI.</para>
/// </summary>
public sealed class CopilotCliChatModel(CopilotCliOptions options) : IChatModel
{
    /// <summary>
    /// Streams a reply. <paramref name="tools"/> and <paramref name="toolCalls"/> are ignored:
    /// Copilot brings its own tools and cannot be asked to emit OpenAI-shaped tool calls, so an
    /// agent on this backend is a voice in the room rather than a hand in the system.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        IReadOnlyList<ToolSpec> tools,
        ICollection<ToolCallRequest>? toolCalls,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.WorkingDirectory);

        var start = new ProcessStartInfo(options.Executable)
        {
            WorkingDirectory = options.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var argument in Arguments(messages))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{options.Executable}'.");
        }

        try
        {
            // Read the error stream concurrently: a CLI that fills its stderr pipe while we are
            // only draining stdout deadlocks, and the failure looks like a hung model.
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);

            while (await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } line)
            {
                if (Delta(line) is { Length: > 0 } text)
                {
                    yield return text;
                }
            }

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = (await errors.ConfigureAwait(false)).Trim();
                throw new InvalidOperationException(
                    $"{options.Executable} exited {process.ExitCode}" +
                    (detail.Length == 0 ? "." : $": {Summarise(detail)}"));
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                // A timed-out or cancelled turn must not leave a model running and billing.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the check and the kill, which is the outcome we wanted.
                }
            }
        }
    }

    private IEnumerable<string> Arguments(IReadOnlyList<ChatTurn> messages)
    {
        yield return "--prompt";
        yield return Render(messages);

        yield return "--output-format";
        yield return "json";

        // Non-interactive mode refuses to run without this. It grants permission; what there is to
        // permit is decided by the tool list below.
        yield return "--allow-all-tools";

        // Never stop to ask a question: there is nobody at a terminal to answer, and a turn that
        // blocks on a prompt is a turn that hangs until the timeout.
        yield return "--no-ask-user";

        if (!options.AllowCopilotTools)
        {
            // No tools at all, and no built-in MCP servers — the GitHub one connects by default,
            // which is both a capability and a route for a room's contents to leave.
            yield return "--available-tools";
            yield return "--disable-builtin-mcps";
        }

        if (options.Model.Length > 0)
        {
            yield return "--model";
            yield return options.Model;
        }

        yield return "--no-color";
        yield return "--log-level";
        yield return "none";
    }

    /// <summary>
    /// The room's context as one prompt. Copilot takes a single string, so the turn structure is
    /// rendered rather than sent — labelled per speaker, because a group chat has more than one
    /// human and an unlabelled transcript loses track of who is being answered.
    /// </summary>
    private static string Render(IReadOnlyList<ChatTurn> messages)
    {
        var prompt = new StringBuilder();

        foreach (var turn in messages)
        {
            var speaker = turn.Role switch
            {
                "system" => "Instructions",
                "assistant" => "You",
                _ => "Them",
            };

            prompt.Append(speaker).Append(": ").AppendLine(turn.Content);
        }

        prompt.AppendLine();
        prompt.Append("Reply as yourself, in the room. Do not prefix your reply with your name.");
        return prompt.ToString();
    }

    /// <summary>
    /// One line of Copilot's JSONL, reduced to the text it adds. Everything else — session
    /// warnings, MCP status, usage checkpoints — is noise to a room.
    /// </summary>
    private static string? Delta(string line)
    {
        if (line.Length == 0 || line[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type) ||
                type.GetString() != "assistant.message_delta" ||
                !root.TryGetProperty("data", out var data))
            {
                return null;
            }

            return data.TryGetProperty("deltaContent", out var content) ? content.GetString() : null;
        }
        catch (JsonException)
        {
            // A line that is not JSON is not a delta. The CLI is free to print whatever it likes.
            return null;
        }
    }

    /// <summary>Enough of a failure to act on, without pasting a stack trace into a chat room.</summary>
    private static string Summarise(string detail)
    {
        var first = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return first.Length <= 200 ? first : first[..200] + "…";
    }

    public void Dispose()
    {
        // Nothing is held between turns; the process lives and dies inside StreamAsync.
    }
}
