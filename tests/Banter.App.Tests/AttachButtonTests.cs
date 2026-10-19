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

    /// <summary>A real file to pick: staging reads its size, so a path to nothing is refused.</summary>
    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "banter-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    /// <summary>
    /// Choosing a file puts it on the next message; it does not send it. Sent the moment it was
    /// picked, it arrived as a message of its own - a bare filename the delegator then answered,
    /// before anybody had written a word about what the file was for.
    /// </summary>
    [Fact]
    public async Task ChoosingAFileStagesItForTheNextMessage()
    {
        var vm = Ready();
        vm.EnableAttach();
        var sent = new List<(string Room, string Path)>();
        var file = TempFile("diagram.png");

        var app = new BanterChatApp(vm)
        {
            FilePicker = new StubFilePicker(file),
            AttachAsync = (room, path) => { sent.Add((room, path)); return Task.CompletedTask; },
        };

        app.PickAttachment();
        await SettleAsync(() => { vm.ApplyPending(); return vm.StagedFor("#main") is not null; });

        Assert.Equal(file, vm.StagedFor("#main")?.Path);
        Assert.Empty(sent);
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
        var file = TempFile("report.pdf");

        var app = new BanterChatApp(vm) { FilePicker = new StubFilePicker(file) };

        app.PickAttachment();

        // A dialog is modal to the user but not to the app: the room can change underneath it,
        // and the file belongs where they were looking when they chose it.
        vm.SwitchTo("#other");
        await SettleAsync(() => { vm.ApplyPending(); return vm.StagedFor("#main") is not null; });

        Assert.NotNull(vm.StagedFor("#main"));
        Assert.Null(vm.StagedFor("#other"));
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
