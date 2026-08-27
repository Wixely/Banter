using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The attach control. It sits on top of <c>/upload</c> rather than replacing it — the slash
/// command still works, and is still the fastest route when you already know the path.
/// </summary>
public sealed class AttachButtonTests
{
    private static ChatViewModel Ready()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        return vm;
    }

    /// <summary>
    /// Waits for the pick to finish. The pick runs off the caller's thread, as a real dialog must,
    /// so a test asserting straight after <c>PickAttachment</c> is asserting on a race — but
    /// waiting a fixed second for each of these would be seven wasted seconds a run.
    /// </summary>
    private static async Task SettleAsync(Func<bool> done)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && !done())
        {
            await Task.Delay(5);
        }
    }

    /// <summary>For the cases whose expected outcome is that nothing happens.</summary>
    private static Task QuietAsync() => Task.Delay(150);

    [Fact]
    public void TheButtonIsHiddenOnAHeadThatCannotOpenADialog()
    {
        var vm = Ready();

        Assert.Contains("hidden", vm.Model.AttachButtonClass);

        vm.EnableAttach();
        Assert.DoesNotContain("hidden", vm.Model.AttachButtonClass);
    }

    [Fact]
    public async Task ChoosingAFileSendsItToTheRoom()
    {
        var vm = Ready();
        vm.EnableAttach();
        var sent = new List<(string Room, string Path)>();

        var app = new BanterChatApp(vm)
        {
            FilePicker = new StubFilePicker(@"C:\notes\diagram.png"),
            AttachAsync = (room, path) => { sent.Add((room, path)); return Task.CompletedTask; },
        };

        app.PickAttachment();
        await SettleAsync(() => sent.Count > 0);

        Assert.Equal(("#main", @"C:\notes\diagram.png"), Assert.Single(sent));
    }

    [Fact]
    public async Task CancellingSendsNothing()
    {
        var vm = Ready();
        vm.EnableAttach();
        var sent = 0;

        var app = new BanterChatApp(vm)
        {
            // Null is the cancel answer, and cancelling is the common case rather than an error.
            FilePicker = new StubFilePicker(null),
            AttachAsync = (_, _) => { sent++; return Task.CompletedTask; },
        };

        app.PickAttachment();
        await QuietAsync();

        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AHeadWithNoPickerNeverOpensOne()
    {
        var vm = Ready();
        var picker = new StubFilePicker("ignored") { IsSupported = false };
        var app = new BanterChatApp(vm) { FilePicker = picker };

        app.PickAttachment();
        await QuietAsync();

        Assert.Empty(picker.Titles);
    }

    [Fact]
    public async Task TheFileGoesToTheRoomThatWasOpenWhenItWasChosen()
    {
        var vm = Ready();
        vm.AddRoom("#other");
        vm.SwitchTo("#main");
        vm.EnableAttach();
        var sent = new List<(string Room, string Path)>();

        var app = new BanterChatApp(vm)
        {
            FilePicker = new StubFilePicker("/tmp/report.pdf"),
            AttachAsync = (room, path) => { sent.Add((room, path)); return Task.CompletedTask; },
        };

        app.PickAttachment();

        // A dialog is modal to the user but not to the app: the room can change underneath it,
        // and the file belongs where they were looking when they chose it.
        vm.SwitchTo("#other");
        await SettleAsync(() => sent.Count > 0);

        Assert.Equal("#main", Assert.Single(sent).Room);
    }

    [Fact]
    public async Task TheDialogSaysWhereTheFileIsGoing()
    {
        var vm = Ready();
        vm.EnableAttach();
        var picker = new StubFilePicker(null);
        var app = new BanterChatApp(vm) { FilePicker = picker };

        app.PickAttachment();
        await SettleAsync(() => picker.Titles.Count > 0);

        Assert.Contains("#main", Assert.Single(picker.Titles));
    }

    [Fact]
    public async Task NothingHappensWithNoRoomOpen()
    {
        var vm = new ChatViewModel();
        vm.EnableAttach();
        var picker = new StubFilePicker("/tmp/x");
        var app = new BanterChatApp(vm) { FilePicker = picker };

        app.PickAttachment();
        await QuietAsync();

        Assert.Empty(picker.Titles);
    }
}
