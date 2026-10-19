using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// A picked file waits for Send and goes with the words written about it, instead of being sent
/// the moment it is picked as a message of its own.
/// </summary>
public sealed class StagedAttachmentTests
{
    private sealed record Sent(string Room, string Text, string Path, string ReplyTo);

    /// <summary>Built for the machine running the test: a Windows path's backslashes are not
    /// separators on Linux, where the name would come back as the whole path.</summary>
    private static readonly string Shot = Path.Combine(Path.GetTempPath(), "pics", "shot.png");

    private static (BanterChatApp App, ChatViewModel Vm, List<Sent> WithFile, List<string> Plain, List<string> Commands) Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.AddRoom("#other");
        vm.SwitchTo("#main");

        var withFile = new List<Sent>();
        var plain = new List<string>();
        var commands = new List<string>();
        var app = new BanterChatApp(vm)
        {
            SendWithFileAsync = (room, text, path, replyTo) =>
            {
                withFile.Add(new Sent(room, text, path, replyTo));
                return Task.CompletedTask;
            },
            SendAsync = (_, text) =>
            {
                plain.Add(text);
                return Task.CompletedTask;
            },
            CommandAsync = (_, text) =>
            {
                commands.Add(text);
                return Task.CompletedTask;
            },
        };
        return (app, vm, withFile, plain, commands);
    }

    [Fact]
    public void AStagedFileIsShownAndSentWithTheWords()
    {
        var (app, vm, withFile, plain, _) = Room();
        vm.StageAttachment("#main", Shot, 2048);

        Assert.Equal("staged", vm.Model.StagedClass);
        Assert.Equal("shot.png · 2 KB", vm.Model.StagedText);

        vm.Model.Composer = "what is this?";
        app.Send();

        Assert.Equal(new Sent("#main", "what is this?", Shot, ""), Assert.Single(withFile));
        Assert.Empty(plain);
        Assert.Equal("staged hidden", vm.Model.StagedClass);
    }

    [Fact]
    public void AFileAloneIsEnoughToSend()
    {
        var (app, vm, withFile, _, _) = Room();
        vm.StageAttachment("#main", Shot, 10);

        app.Send();

        Assert.Equal("", Assert.Single(withFile).Text);
    }

    [Fact]
    public void AStagedFileBelongsToTheRoomItWasPickedFor()
    {
        var (app, vm, withFile, plain, _) = Room();
        vm.StageAttachment("#main", Shot, 10);

        vm.SwitchTo("#other");
        Assert.Equal("staged hidden", vm.Model.StagedClass);
        vm.Model.Composer = "unrelated";
        app.Send();
        Assert.Empty(withFile);
        Assert.Equal(["unrelated"], plain);

        vm.SwitchTo("#main");
        Assert.Equal("staged", vm.Model.StagedClass);
    }

    [Fact]
    public void ACommandLeavesTheFileForTheNextMessage()
    {
        var (app, vm, withFile, _, commands) = Room();
        vm.StageAttachment("#main", Shot, 10);

        vm.Model.Composer = "/tasks";
        app.Send();

        Assert.Equal(["/tasks"], commands);
        Assert.Empty(withFile);
        Assert.NotNull(vm.StagedFor("#main"));
    }

    [Fact]
    public void TheCrossTakesItOff()
    {
        var (app, vm, withFile, _, _) = Room();
        vm.StageAttachment("#main", Shot, 10);

        vm.Unstage("#main");
        app.Send();

        Assert.Equal("staged hidden", vm.Model.StagedClass);
        Assert.Empty(withFile);
    }

    [Fact]
    public void ARowForAFileAlreadyHeldShowsItAtOnce()
    {
        var (_, vm, _, _, _) = Room();
        var path = Path.Combine(Path.GetTempPath(), "banter-held.png");
        vm.SetAttachmentInfo("f1", "held.png", 100);
        vm.SetInlineImage("f1", path);

        // The echo of our own message arrives after the upload already cached the file.
        var row = vm.Append("#main", "alice", "here", 0, fileId: "f1");

        Assert.Equal("inline-image", row.ImageClass);
        Assert.Equal(new Uri(path).AbsoluteUri, row.ImageSrc);
        Assert.Equal("held.png (100 B)", row.AttachText);
    }
}
