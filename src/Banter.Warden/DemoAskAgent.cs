using System.Runtime.CompilerServices;
using Banter.Agents.Sdk;

namespace Banter.Warden;

/// <summary>
/// An agent that exists to be answered (PLAN §8c-a). It asks scripted questions and reports what
/// came back — no model, no endpoint, no tokens.
///
/// <para>Every other agent here needs an LLM to decide to ask something, which makes the controls
/// awkward to look at and impossible to test: a small local model calling a structured tool with
/// well-formed JSON is a coin flip, and a failed demo tells you nothing about whether the UI
/// works. This one always asks, always asks the same thing, and asks a shape you choose.</para>
///
/// <para>It goes through the real <c>ask_operator</c> tool rather than reaching for the wire
/// directly, so what is being demonstrated is the path an actual agent takes.</para>
/// </summary>
public sealed class DemoAskAgent(BanterAgentOptions options) : BanterAgent(options)
{
    /// <summary>The scenarios, keyed by the word that triggers them.</summary>
    private static readonly (string Word, string What, string Json)[] Scenarios =
    [
        ("confirm", "a yes/no confirmation", """
            {"questions":[{"key":"deploy","header":"Deploy",
              "question":"The migration is ready. Shall I run it against staging?",
              "options":[
                {"value":"yes","label":"Yes","description":"Run it now."},
                {"value":"no","label":"No","description":"Leave it; I will run it myself."}]}]}
            """),

        ("pick", "one of several (radio)", """
            {"questions":[{"key":"who","header":"Who",
              "question":"Who should take the database slice?",
              "options":[
                {"value":"dagger","label":"dagger","description":"Local, cleared for sensitive rooms."},
                {"value":"scout","label":"scout","description":"Frontier. Nothing sensitive may reach it."},
                {"value":"me","label":"You","description":"Hand it back and I will do the rest."}]}]}
            """),

        ("many", "several at once (checkboxes)", """
            {"questions":[{"key":"sections","header":"Sections","multi_select":true,
              "question":"Which sections should the summary include?",
              "options":[
                {"value":"intro","label":"Introduction","description":"What the change is for."},
                {"value":"risks","label":"Risks","description":"What could go wrong on the night."},
                {"value":"rollback","label":"Rollback","description":"How to undo it."},
                {"value":"timings","label":"Timings","description":"How long each step takes."}]}]}
            """),

        ("tabs", "three questions as tabs", """
            {"questions":[
              {"key":"who","header":"Who","question":"Who takes the database slice?",
               "options":[{"value":"dagger","label":"dagger"},{"value":"scout","label":"scout"}]},
              {"key":"when","header":"When","question":"Before or after the review?",
               "options":[
                 {"value":"before","label":"Before","description":"Blocks the review until it is done."},
                 {"value":"after","label":"After","description":"Slower, but the review stays unblocked."}]},
              {"key":"tell","header":"Tell","multi_select":true,
               "question":"Who should I tell when it is finished?",
               "options":[
                 {"value":"room","label":"This room"},{"value":"admin","label":"The admin"},
                 {"value":"nobody","label":"Nobody"}]}]}
            """),

        ("write", "no options at all — type the answer", """
            {"questions":[{"key":"name","header":"Name",
              "question":"What should I call the side room I am about to open?","options":[]}]}
            """),
    ];

    private static string Menu =>
        "I ask questions so you can answer them. Say my name and one of: "
        + string.Join(", ", Scenarios.Select(s => $"**{s.Word}** ({s.What})"))
        + ". Or **all** to ask them one after another.";

    protected override async IAsyncEnumerable<string> RespondAsync(
        string room, string sender, string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wanted = Scenarios
            .Where(s => prompt.Contains(s.Word, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (prompt.Contains("all", StringComparison.OrdinalIgnoreCase))
        {
            wanted = [.. Scenarios];
        }

        if (wanted.Count == 0)
        {
            yield return Menu;
            yield break;
        }

        foreach (var scenario in wanted)
        {
            // The turn waits here, exactly as a real agent's would: the room does not, which is
            // the whole point of the design and the thing worth watching while this sits open.
            var answer = await InvokeToolAsync("ask_operator", scenario.Json, room, cancellationToken)
                .ConfigureAwait(false);

            // Said back so the round trip is visible. Somebody testing the free-text box needs to
            // see that what they typed actually reached the agent, not just that the panel closed.
            yield return $"[{scenario.Word}] {answer.Content}";
        }
    }
}
