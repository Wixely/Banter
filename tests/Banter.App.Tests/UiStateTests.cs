using Banter.App;
using Banter.Protocol;
using CupriFace;
using CupriFace.Diagnostics;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Nothing is squeezed out of sight in the states the app gets INTO - the drawer open, a panel
/// up, a question asked, a form filled - rather than only the state each page loads in, which is
/// all <see cref="PhoneFitTests"/> can look at.
///
/// <para>Two layers. The named states are the ones already known to crowd a phone: a drawer over
/// the chat, the room panel's four settings, a confirm over everything. The crawl is for the ones
/// nobody named: it clicks every control on every page, each on a fresh document, and checks
/// after each - CupriFace 0.45.0's <c>InteractionCrawl</c>.</para>
/// </summary>
public sealed class UiStateTests(ITestOutputHelper output)
{
    private static readonly (int W, int H)[] Sizes = [(412, 915), (915, 412), (1280, 800)];

    public static TheoryData<string, int, int> States
    {
        get
        {
            var data = new TheoryData<string, int, int>();
            foreach (var state in new[] { "drawer", "room-panel", "confirm", "agent-form", "user-form", "about", "members" })
            {
                foreach (var (w, h) in Sizes)
                {
                    data.Add(state, w, h);
                }
            }

            return data;
        }
    }

    public static TheoryData<string, int, int> PagesToCrawl
    {
        get
        {
            var data = new TheoryData<string, int, int>();
            foreach (var page in AppPages.Names)
            {
                data.Add(page, 412, 915);
                data.Add(page, 1280, 800);
            }

            return data;
        }
    }

    /// <summary>The app put into one of the states, as a person would get it there.</summary>
    private static BanterChatApp In(string state, int width)
    {
        var app = AppPages.Showing(state switch
        {
            "agent-form" => "agents",
            "user-form" => "users",
            "about" => "settings",
            _ => "chat",
        });
        var vm = app.ViewModel;
        var narrow = width < 700;

        switch (state)
        {
            case "drawer":
                vm.ToggleRooms(narrow);
                break;
            case "room-panel":
                vm.SetRoomListing([("#main", null, 3, RoomDisclosure.Members), ("#other", "#main", 2, RoomDisclosure.Children)]);
                vm.ShowRoomPanel(true);
                break;
            case "confirm":
                vm.ConfirmCloseRoom("#other");
                break;
            case "members":
                // The roster with people and agents in it, which on a phone is half of the drawer.
                vm.SetRoomUsers("#main", [("alice", ""), ("bob", ""), ("a-person-with-a-long-name", "")]);
                vm.SetAgents("#main", [("dagger", true, "chat", true), ("scout-with-a-longer-nick", false, "web, search", false)]);
                vm.ToggleRooms(narrow);
                break;
            case "agent-form":
                vm.SetAgentIdentities([new AgentIdentityPayload(
                    "dagger", ["#main"], ["chat", "notes"], "local", "sensitive", true, "3f2a 91c0 be47 1d08", false,
                    Persona: "Dry and brief.", LastSdk: "0.15.1", LastSeenAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds())]);
                vm.SelectAdminAgent("dagger");
                break;
            case "user-form":
                vm.SetUsers([new UserAccountPayload("root", true), new UserAccountPayload("nell", false)]);
                vm.SelectAdminUser("nell");
                break;
            case "about":
                vm.ShowSettingsSection("about");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "no such state");
        }

        return app;
    }

    private static CupriDocument Fresh(BanterChatApp app, int width)
    {
        var doc = app.CreateDocument();
        if (width < 700)
        {
            doc.InputProfile = InputProfile.Touch;
        }

        return doc;
    }

    [Theory]
    [MemberData(nameof(States))]
    public void NothingIsSqueezedOutOfExistenceInThatState(string state, int w, int h)
    {
        var app = In(state, w);
        using var doc = Fresh(app, w);
        var report = CupriDoctor.Check(doc, w, h);

        var lost = report.Findings.Where(f => PhoneFitTests.Invisible.Contains(f.Code)).ToList();
        foreach (var f in lost)
        {
            output.WriteLine(f.ToString());
        }

        Assert.True(lost.Count == 0, $"{lost.Count} thing(s) invisible with the {state} up at {w}x{h}");
    }

    [Theory]
    [MemberData(nameof(PagesToCrawl))]
    public void EveryClickLeavesTheScreenWhole(string page, int w, int h)
    {
        var report = InteractionCrawl.Click(
            () => Fresh(AppPages.Showing(page), w), w, h,
            new CrawlOptions { MaxClicks = 60 });

        output.WriteLine($"{report.Checks} documents checked on '{page}' at {w}x{h}");
        var lost = report.Findings.Where(f => PhoneFitTests.Invisible.Contains(f.Code)).ToList();
        foreach (var f in lost)
        {
            output.WriteLine(f.ToString());
        }

        Assert.True(lost.Count == 0, $"{lost.Count} thing(s) invisible after a click on '{page}' at {w}x{h}");
    }
}
