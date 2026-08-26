using System.Net;
using System.Text;
using System.Text.Json;
using Banter.Agents.Sdk;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// Reassembling <c>tool_calls</c> from a token stream. Servers split a single call across many
/// chunks — often mid-word in the name and mid-token in the JSON arguments — and interleave the
/// chunks of parallel calls. Get the reassembly wrong and the agent asks the server to run a
/// truncated tool name with half an argument object, which is a very confusing failure.
/// </summary>
public sealed class OpenAiToolCallTests
{
    /// <summary>Serves the exact server-sent-events lines it is given, then <c>[DONE]</c>.</summary>
    private sealed class ScriptedHandler(IEnumerable<object> chunks) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var sse = new StringBuilder();
            foreach (var chunk in chunks)
            {
                sse.Append("data: ").Append(JsonSerializer.Serialize(chunk)).Append("\n\n");
            }

            sse.Append("data: [DONE]\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString()) };
        }
    }

    private static LlmChatAgentOptions Options => new()
    {
        Endpoint = new Uri("http://localhost:1234/v1"),
        Model = "test",
    };

    /// <summary>One SSE chunk carrying a tool_calls delta.</summary>
    private static object Call(int index, string? id = null, string? name = null, string? arguments = null) =>
        new
        {
            choices = new[]
            {
                new
                {
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new { index, id, function = new { name, arguments } },
                        },
                    },
                },
            },
        };

    private static object Text(string content) =>
        new { choices = new[] { new { delta = new { content } } } };

    private static async Task<(string Content, List<ToolCallRequest> Calls, ScriptedHandler Handler)> RunAsync(
        IEnumerable<object> chunks, IReadOnlyList<ToolSpec>? tools = null)
    {
        var handler = new ScriptedHandler(chunks);
        using var client = new OpenAiChatClient(Options, handler);
        var calls = new List<ToolCallRequest>();
        var text = new StringBuilder();

        await foreach (var delta in client.StreamAsync([ChatTurn.User("go")], tools ?? [], calls))
        {
            text.Append(delta);
        }

        return (text.ToString(), calls, handler);
    }

    [Fact]
    public async Task ACallSplitAcrossChunksIsReassembled()
    {
        var (_, calls, _) = await RunAsync(
        [
            Call(0, id: "call_1", name: "gh_list"),
            Call(0, name: "_issues"),
            Call(0, arguments: "{\"repo\":"),
            Call(0, arguments: "\"banter\"}"),
        ]);

        var call = Assert.Single(calls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("gh_list_issues", call.Name);
        Assert.Equal("""{"repo":"banter"}""", call.Arguments);
    }

    [Fact]
    public async Task InterleavedParallelCallsStaySeparate()
    {
        var (_, calls, _) = await RunAsync(
        [
            Call(0, id: "a", name: "read_file"),
            Call(1, id: "b", name: "gh_list_issues"),
            Call(0, arguments: "{\"path\":"),
            Call(1, arguments: """{"repo":"x"}"""),
            Call(0, arguments: "\"/tmp\"}"),
        ]);

        Assert.Equal(2, calls.Count);
        Assert.Equal(("a", "read_file", """{"path":"/tmp"}"""), (calls[0].Id, calls[0].Name, calls[0].Arguments));
        Assert.Equal(("b", "gh_list_issues", """{"repo":"x"}"""), (calls[1].Id, calls[1].Name, calls[1].Arguments));
    }

    [Fact]
    public async Task TextAndACallInTheSameTurnBothSurvive()
    {
        var (content, calls, _) = await RunAsync(
        [
            Text("Let me check"),
            Text(" that."),
            Call(0, id: "c", name: "gh_list_issues", arguments: "{}"),
        ]);

        // Models routinely narrate before calling. Dropping the narration loses the thread of
        // what the agent thought it was doing.
        Assert.Equal("Let me check that.", content);
        Assert.Equal("gh_list_issues", Assert.Single(calls).Name);
    }

    [Fact]
    public async Task ACallThatNeverGotANameIsDiscarded()
    {
        var (_, calls, _) = await RunAsync([Call(0, id: "truncated", arguments: "{}")]);

        // A stream cut short leaves a nameless call. Passing it on would ask the server to run
        // the empty string.
        Assert.Empty(calls);
    }

    [Fact]
    public async Task NoToolsMeansNoToolsFieldOnTheWire()
    {
        var (_, _, handler) = await RunAsync([Text("hi")]);

        // Some local servers reject a request carrying an empty tools array outright, so the
        // no-tools case must look exactly like it did before tools existed.
        Assert.DoesNotContain("\"tools\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task ASchemaIsSentAsJsonNotAsAQuotedString()
    {
        var schema = """{"type":"object","properties":{"repo":{"type":"string"}}}""";

        var (_, _, handler) = await RunAsync(
            [Text("hi")],
            [new ToolSpec("gh_list_issues", "List issues", schema)]);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var parameters = body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");

        // A schema the model reads as a string is a schema it ignores, and it then guesses
        // argument names.
        Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
        Assert.Equal("string", parameters.GetProperty("properties").GetProperty("repo").GetProperty("type").GetString());
    }

    [Fact]
    public async Task AnUnparseableSchemaLeavesTheOtherToolsIntact()
    {
        var (_, _, handler) = await RunAsync(
            [Text("hi")],
            [
                new ToolSpec("broken", "Bad schema", "not json at all"),
                new ToolSpec("fine", "Good schema", """{"type":"object","properties":{}}"""),
            ]);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var tools = body.RootElement.GetProperty("tools");

        Assert.Equal(2, tools.GetArrayLength());
        Assert.Equal(
            "object",
            tools[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task AToolResultTurnCarriesItsCallId()
    {
        var handler = new ScriptedHandler([Text("done")]);
        using var client = new OpenAiChatClient(Options, handler);

        await foreach (var _ in client.StreamAsync(
            [
                ChatTurn.User("go"),
                ChatTurn.AssistantCalls("checking", [new ToolCallRequest("call_9", "gh_list_issues", "{}")]),
                ChatTurn.Tool("call_9", "3 issues"),
            ],
            [], toolCalls: null))
        {
        }

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = body.RootElement.GetProperty("messages");

        // The model matches results to calls by id. Without it the result attaches to the wrong
        // call, or the server rejects the conversation outright.
        Assert.Equal("call_9", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("call_9", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
    }
}
