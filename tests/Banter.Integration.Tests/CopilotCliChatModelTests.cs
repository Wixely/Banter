using Banter.Agents.Sdk;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// The Copilot CLI backend, driven against a stand-in that replays real recorded output rather
/// than the CLI itself: a test that spent a premium request per assertion would not be one anybody
/// runs. The JSONL below is verbatim from `copilot -p ... --output-format json`.
/// </summary>
public sealed class CopilotCliChatModelTests
{
    /// <summary>
    /// A script that prints canned lines and exits, standing in for the CLI. `cmd /c` on Windows
    /// and `sh -c` elsewhere, because the point is the parsing rather than the platform.
    /// </summary>
    private static CopilotCliOptions Replaying(string jsonl, int exitCode = 0)
    {
        var root = Path.Combine(Path.GetTempPath(), "banter-copilot-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(root);

        if (OperatingSystem.IsWindows())
        {
            // A batch file: `echo` per line, then the exit code. Quoting is the fiddly part, so the
            // payload goes to a file and is `type`d back rather than echoed.
            var payload = Path.Combine(root, "out.jsonl");
            File.WriteAllText(payload, jsonl);
            var script = Path.Combine(root, "fake-copilot.cmd");
            File.WriteAllText(script, $"@echo off\r\ntype \"{payload}\"\r\nexit /b {exitCode}\r\n");
            return new CopilotCliOptions { Executable = script, WorkingDirectory = root };
        }

        var shell = Path.Combine(root, "fake-copilot.sh");
        File.WriteAllText(shell, $"#!/bin/sh\ncat <<'EOF'\n{jsonl}\nEOF\nexit {exitCode}\n");
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new CopilotCliOptions { Executable = shell, WorkingDirectory = root };
    }

    private static async Task<string> ReplyAsync(CopilotCliOptions options)
    {
        using var model = new CopilotCliChatModel(options);
        var reply = "";

        await foreach (var piece in model.StreamAsync([ChatTurn.User("hello")], [], null))
        {
            reply += piece;
        }

        return reply;
    }

    [Fact]
    public async Task ItYieldsTheAssistantDeltasAndNothingElse()
    {
        // Verbatim shape, trimmed to the lines that matter: the session and MCP chatter around a
        // reply is noise to a room, and the deltas are what a person actually sees arriving.
        var reply = await ReplyAsync(Replaying(
            """
            {"type":"session.warning","data":{"warningType":"policy","message":"Third-party MCP servers are disabled"},"ephemeral":true}
            {"type":"session.mcp_server_status_changed","data":{"serverName":"github-mcp-server","status":"connected"},"ephemeral":true}
            {"type":"assistant.message_start","data":{"messageId":"m1"},"ephemeral":true}
            {"type":"assistant.message_delta","data":{"messageId":"m1","deltaContent":"p"},"ephemeral":true}
            {"type":"assistant.message_delta","data":{"messageId":"m1","deltaContent":"ong"},"ephemeral":true}
            {"type":"assistant.message","data":{"messageId":"m1","content":"pong","toolRequests":[]}}
            {"type":"session.usage_checkpoint","data":{"totalPremiumRequests":7.5}}
            {"type":"result","sessionId":"a7a2dfee","exitCode":0}
            """));

        // "pong" once: from the deltas. The complete assistant.message that follows repeats the
        // same text, and counting it too would double every reply.
        Assert.Equal("pong", reply);
    }

    [Fact]
    public async Task ItIgnoresLinesThatAreNotJson()
    {
        // The CLI is free to print whatever it likes alongside the stream; a banner must not end
        // up in the room, and must not throw either.
        var reply = await ReplyAsync(Replaying(
            """
            Welcome to Copilot!
            {"type":"assistant.message_delta","data":{"deltaContent":"hi"}}
            not json at all
            """));

        Assert.Equal("hi", reply);
    }

    [Fact]
    public async Task AFailedRunIsReportedRatherThanReturningNothing()
    {
        var options = Replaying("""{"type":"result","exitCode":1}""", exitCode: 1);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ReplyAsync(options));

        // Silence would look like a model with nothing to say, which is the one thing a failure
        // must not be mistaken for.
        Assert.Contains("exited 1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItsOwnToolsAreOffUnlessAskedFor()
    {
        // The default matters more than most: this agent is prompted by whatever a room types at
        // it, so its tools are a room's tools.
        Assert.False(new CopilotCliOptions().AllowCopilotTools);
    }

    [Fact]
    public void ItRunsSomewhereOtherThanTheCallersDirectory()
    {
        // Without file access being confined somewhere harmless, a chat room would have a foothold
        // in whatever tree the agent happened to be started from.
        var working = new CopilotCliOptions().WorkingDirectory;

        Assert.NotEqual(Directory.GetCurrentDirectory(), working);
        Assert.StartsWith(Path.GetTempPath(), working, StringComparison.OrdinalIgnoreCase);
    }
}
