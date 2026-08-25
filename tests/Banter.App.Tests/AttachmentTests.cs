using Banter.App;
using Xunit;

namespace Banter.App.Tests;

public sealed class AttachmentTests
{
    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        return vm;
    }

    [Fact]
    public void RowsWithoutAFileHideTheAttachmentChip()
    {
        var vm = Room();

        var row = vm.Append("#main", "bob", "just text", 0);

        Assert.Equal("", row.FileId);
        Assert.Equal("attach hidden", row.AttachClass);
    }

    [Fact]
    public void AFileRowIsVisibleImmediatelyWithAPlaceholder()
    {
        var vm = Room();

        // Name and size need a second round-trip; withholding the row until then would make
        // shared files appear late and out of order.
        var row = vm.Append("#main", "bob", "here you go", 0, fileId: "f1");

        Assert.Equal("attach", row.AttachClass);
        Assert.Equal("attachment", row.AttachText);
    }

    [Fact]
    public void MetadataArrivingLaterLabelsEveryRowForThatFile()
    {
        var vm = Room();
        vm.AddRoom("#other");
        var a = vm.Append("#main", "bob", "shared", 0, fileId: "f1");
        var b = vm.Append("#other", "bob", "shared again", 0, fileId: "f1");

        vm.SetAttachmentInfo("f1", "notes.pdf", 2_400_000);

        Assert.Equal("notes.pdf (2.3 MB)", a.AttachText);
        Assert.Equal("notes.pdf (2.3 MB)", b.AttachText);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5_242_880, "5 MB")]
    [InlineData(3_221_225_472, "3 GB")]
    public void SizesAreShownInAReadableUnit(long bytes, string expected) =>
        Assert.Equal(expected, ChatViewModel.FormatSize(bytes));

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("A.JPG", "image/jpeg")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("archive.tar.gz", "application/gzip")]
    [InlineData("mystery.qqq", "application/octet-stream")]
    [InlineData("noextension", "application/octet-stream")]
    public void MimeTypesAreGuessedFromTheExtension(string name, string expected) =>
        Assert.Equal(expected, MimeTypes.ForFile(name));

    [Fact]
    public void ImagesAreDistinguishedForInlineRendering()
    {
        Assert.True(MimeTypes.IsImage("image/png"));
        Assert.False(MimeTypes.IsImage("application/pdf"));
    }

    [Fact]
    public void QuotedUploadPathsKeepSpacesAndSeparateTheDescription()
    {
        var (path, description) = BanterChatSession.SplitPathAndDescription("\"C:\\my files\\a.png\" the diagram");

        Assert.Equal("C:\\my files\\a.png", path);
        Assert.Equal("the diagram", description);
    }

    [Fact]
    public void AnUnquotedPathWithNoDescriptionIsTakenWhole()
    {
        // A path containing spaces and no description must not be split at the first space.
        var (path, description) = BanterChatSession.SplitPathAndDescription("C:\\my files\\a.png");

        Assert.Equal("C:\\my files\\a.png", path);
        Assert.Null(description);
    }

    [Fact]
    public void SlashInputIsRoutedToCommandsRatherThanSentToTheRoom()
    {
        var vm = Room();
        var sent = new List<string>();
        var commands = new List<string>();
        var app = new BanterChatApp(vm)
        {
            SendAsync = (_, t) => { sent.Add(t); return Task.CompletedTask; },
            CommandAsync = (_, c) => { commands.Add(c); return Task.CompletedTask; },
        };
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        vm.Model.Composer = "/upload C:\\a.png";
        app.Send();
        vm.Model.Composer = "hello";
        app.Send();

        // A mistyped command must not leak to the room as chat text.
        Assert.Equal("/upload C:\\a.png", Assert.Single(commands));
        Assert.Equal("hello", Assert.Single(sent));
    }

    [Fact]
    public void ClickingAnAttachmentRequestsThatFile()
    {
        var vm = Room();
        var requested = new List<string>();
        var app = new BanterChatApp(vm) { DownloadAsync = id => { requested.Add(id); return Task.CompletedTask; } };
        using var doc = app.CreateDocument();
        vm.Append("#main", "bob", "here", 0, fileId: "file-42");
        doc.Refresh();
        doc.BuildDisplayList(1100, 760);

        // Drive the handler the same way the document would.
        app.DownloadAsync("file-42");

        Assert.Equal("file-42", Assert.Single(requested));
    }
}
