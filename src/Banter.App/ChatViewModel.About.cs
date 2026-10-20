using System.Reflection;

namespace Banter.App;

/// <summary>
/// The About tab: what this is and which build, where it lives, and a short account of the rules
/// that are not obvious from using it - the ones people otherwise learn by being surprised.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Where Banter's source and releases are.</summary>
    public const string ProjectUrl = "https://github.com/Wixely/Banter";

    /// <summary>
    /// A version as people read it: "0.13.2", with the commit it was built from when the build
    /// recorded one - "0.13.2 (cd91672)". The informational version is what CI stamps with the
    /// release number; the assembly version is only a fallback, for a build that stamped nothing.
    /// </summary>
    public static string VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not { Length: > 0 })
        {
            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }

        var plus = informational.IndexOf('+');
        if (plus < 0)
        {
            return informational;
        }

        var commit = informational[(plus + 1)..];
        return $"{informational[..plus]} ({(commit.Length > 7 ? commit[..7] : commit)})";
    }

    private void FillAbout()
    {
        Model.AboutVersion = VersionOf(typeof(ChatViewModel).Assembly);
        Model.AboutEngine = VersionOf(typeof(CupriFace.CupriDocument).Assembly);
        Model.HelpItems = [.. Help.Select(h => new HelpRow { Title = h.Title, Text = h.Text })];
    }

    /// <summary>
    /// The rules worth knowing before they surprise you. Short on purpose: each says what happens
    /// and what to do about it, and the room itself says the rest when it matters.
    /// </summary>
    private static readonly (string Title, string Text)[] Help =
    [
        ("Who answers in a room",
            "Each room elects one agent, the delegator, which reads what people say and decides which agent should answer. "
            + "Local agents are preferred. The pill at the top right says who it is; a green dot means somebody is running the room."),
        ("Asking an agent directly",
            "@name reaches that agent whatever the delegator would have done. In the agent list, the ticks choose who acts on "
            + "your messages in this room - the delegator by default; Reset puts it back."),
        ("Third-party agents",
            "A frontier agent runs on somebody else's servers. An amber mark in the message box means what you are writing "
            + "would reach one. A third party checks a request is safe to receive before answering, and is not sent your files."),
        ("Room sensitivity",
            "Each room is Public, Internal or Sensitive (the default): what may be said in it, and so which agents may be "
            + "brought in. A side room starts at its room's level, and an agent already in that room may be invited into its "
            + "side rooms whatever its clearance. Only an admin changes a room's sensitivity, under its ... menu or /sensitivity; "
            + "an agent's clearance is set on the agents page."),
        ("R and I beside the delegator",
            "R is a republic: the delegator is elected the usual way. I is an interregnum: a local delegator left, so the seat is "
            + "held for a local agent and no third party can take it. An admin can end it from the I."),
        ("Agents talking to each other",
            "In an ordinary room agents answer the delegator when it names them. Whether the delegator answers an agent that "
            + "@-names it back is the room's parley, under its ... menu: Pairs (the default - yes, while they are the only two "
            + "agents in the room), None (never) or Open (always). Whatever it says, the server stops relaying agents after "
            + "twelve of their messages in a row with nobody speaking, and after twenty in a minute, until a person speaks."),
        ("Side rooms",
            "Rooms sit in a tree. A side room opened for some work lets agents talk to each other freely, whatever the parley "
            + "says. Its delegator answers people who name nobody. /create #name makes one under the room you are in."),
        ("Who may draw on a room",
            "Everybody in a room reads everything. Whether an agent may draw on it from ANOTHER room depends on the room: Members "
            + "(its agents, from anywhere), Isolated (nowhere else), Children (rooms beneath it) or Global (any agent - be careful). "
            + "See it under the room's ... menu."),
        ("Files",
            "An attached file waits for Send, so your words go with it. Agents are shown images and small text files. Images in an "
            + "agent's reply only ever come from Banter's own storage; a web address becomes a link and is not loaded."),
        ("Who can change what",
            "Admins can add and remove members, close and move rooms, and change any room's settings. A room's delegator can "
            + "manage the agents and settings of the rooms it runs, and the delegator of #main can do so in every room - "
            + "except a room's sensitivity, which only an admin sets."),
        ("Commands",
            "Type /help in any room for the list: /create, /join, /move, /close, /add, /remove, /disclosure, /parley, /regime and more."),
    ];
}
