using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Every screen the app has, and the sizes worth laying them out at.
///
/// <para>Shared because a whole-app check that only ever looked at the screen the app opens on is
/// how four broken management pages went unnoticed through a round of responsive work: the chat
/// screen was clean at every size while agents, users, work and settings were not.</para>
/// </summary>
public static class AppPages
{
    public static readonly string[] Names =
        ["chat", "connect", "tools", "agents", "users", "work", "settings"];

    /// <summary>Density-independent pixels, which is what the Android host hands <c>Present</c>
    /// after dividing by density. A mid-size phone portrait, the same phone landscape, and a small
    /// tablet.</summary>
    public static readonly (int W, int H)[] Small = [(412, 915), (915, 412), (800, 1280)];

    public static TheoryData<string> Each
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var page in Names)
            {
                data.Add(page);
            }

            return data;
        }
    }

    /// <summary>Every page on every one of <paramref name="screens"/> — the cross product, because
    /// a page that only breaks in landscape is still broken.</summary>
    public static TheoryData<string, int, int> On(params (int W, int H)[] screens)
    {
        var data = new TheoryData<string, int, int>();
        foreach (var page in Names)
        {
            foreach (var (w, h) in screens)
            {
                data.Add(page, w, h);
            }
        }

        return data;
    }

    public static TheoryData<string, int, int> OnSmall => On(Small);

    /// <summary>The small screens plus a desktop window. Touch targets need this: a touch laptop
    /// is coarse at 1920, and a size-only check would have called it a mouse.</summary>
    public static TheoryData<string, int, int> OnAnySize => On([.. Small, (1280, 800)]);

    private static ChatViewModel Furnished()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.AddRoom("#other");
        vm.SwitchTo("#main");
        vm.Connected("tcp://host:7770", "alice");
        vm.SetIsAdmin(true);
        vm.Append("#main", "dagger", "hello", 0);
        return vm;
    }

    /// <summary>The app with one page showing. Admin, signed in, and with a room that has
    /// something in it — the state every one of these screens is reached from.</summary>
    public static BanterChatApp Showing(string page)
    {
        var vm = Furnished();
        switch (page)
        {
            case "chat": break;
            case "connect": vm.ShowConnect("", ""); break;
            case "tools": vm.ShowToolPanel(true); break;
            case "agents": vm.ShowAgentsPanel(true); break;
            case "users": vm.ShowUsersPanel(true); break;
            case "work": vm.ShowWorkPanel(true); break;
            case "settings":
                vm.SetVoiceSettings(
                    "local", "en", "dagger", "http://localhost:1234", "localhost:10200",
                    [("aiden", "Aiden"), ("bree", "Bree")], new Dictionary<string, string>());
                vm.ShowSettingsPanel(true);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(page), page, "no such page");
        }

        return new BanterChatApp(vm);
    }
}
