using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The right-click menu aims at the message under the pointer, driven through a real document with
/// a real context dispatch — the thing that cannot be checked by reading the code, because the
/// first version of it worked in a browser and did nothing on the desktop (CupriFace#85).
/// </summary>
public sealed class ContextMenuTargetTests
{
    private const int Width = 1000;
    private const int Height = 700;

    private static (BanterChatApp App, ChatViewModel Vm) Room(string nick = "alice")
    {
        var vm = new ChatViewModel();
        vm.SetNick(nick);
        vm.AddRoom("#main");
        return (new BanterChatApp(vm), vm);
    }

    /// <summary>
    /// A point that really is inside a message row, found by asking the document what is under it
    /// rather than by reading the row's own X/Y — those are laid out relative to the scroller, so
    /// using them directly aims the click somewhere else entirely.
    /// </summary>
    private static (float X, float Y) PointOn(CupriFace.CupriDocument doc, string messageId)
    {
        for (var y = 0f; y < Height; y += 4)
        {
            for (var x = 0f; x < Width; x += 8)
            {
                if (doc.HitTest(x, y)?.Element?.Closest("[data-msg]")?.GetAttribute("data-msg") == messageId)
                {
                    return (x, y);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"nothing painted belongs to data-msg=\"{messageId}\"");
    }

    [Fact]
    public void RightClickingAMessageAimsTheMenuAtIt()
    {
        var (app, vm) = Room();
        vm.Append("#main", "alice", "mine", 0, id: "m1");
        vm.Append("#main", "bob", "theirs", 0, id: "m2");

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "m1");
        Assert.True(doc.DispatchContextMenu(x, y), "the menu should open over a message");

        // Ours: both items offered.
        Assert.DoesNotContain("hidden", vm.Model.EditItemClass, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", vm.Model.DeleteItemClass, StringComparison.Ordinal);
    }

    [Fact]
    public void OverSomebodyElsesMessageOnlyDeleteIsOffered()
    {
        var (app, vm) = Room();
        vm.Append("#main", "alice", "mine", 0, id: "m1");
        vm.Append("#main", "bob", "theirs", 0, id: "m2");

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "m2");
        doc.DispatchContextMenu(x, y);

        // Edit is withheld because the server refuses anyone but the author, and an item that
        // always fails is worse than one that is not there. Delete stays: an admin may use it,
        // and which of those applies is the server's to decide.
        Assert.Contains("hidden", vm.Model.EditItemClass, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", vm.Model.DeleteItemClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMenuAimsAtWhicheverMessageWasClicked()
    {
        var (app, vm) = Room();
        vm.Append("#main", "bob", "theirs", 0, id: "m1");
        vm.Append("#main", "alice", "mine", 0, id: "m2");

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // Both points found BEFORE either right-click: an open menu paints over the timeline, so
        // looking for the second row afterwards finds the menu instead of the message.
        var (x2, y2) = PointOn(doc, "m2");
        var (x1, y1) = PointOn(doc, "m1");

        // Second row first, then the first: the target must follow the pointer rather than stick
        // to whatever was hit earliest.
        doc.DispatchContextMenu(x2, y2);
        Assert.DoesNotContain("hidden", vm.Model.EditItemClass, StringComparison.Ordinal);

        // Dismiss, then repaint. An open menu is laid out over the timeline, so until the next
        // frame a right-click "on" the message underneath still lands on the menu. The running app
        // paints every frame; a headless document only when asked.
        doc.DispatchClick(2, 2);
        doc.BuildDisplayList(Width, Height);

        doc.DispatchContextMenu(x1, y1);
        Assert.Contains("hidden", vm.Model.EditItemClass, StringComparison.Ordinal);
    }

    [Fact]
    public void OverADeletedMessageNeitherIsOffered()
    {
        var (app, vm) = Room();
        vm.Append("#main", "alice", "gone", 0, id: "m1");
        vm.MarkDeleted("#main", "m1");

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "m1");
        doc.DispatchContextMenu(x, y);

        Assert.Contains("hidden", vm.Model.EditItemClass, StringComparison.Ordinal);
        Assert.Contains("hidden", vm.Model.DeleteItemClass, StringComparison.Ordinal);
    }
}
