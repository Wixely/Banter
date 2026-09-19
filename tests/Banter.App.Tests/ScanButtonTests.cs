using Banter.App;
using CupriFace.Dom;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The connect screen's offer to scan a code, and what it does with one.
/// </summary>
public sealed class ScanButtonTests
{
    private static ChatViewModel Connecting()
    {
        var vm = new ChatViewModel();
        vm.ShowConnect("", "");
        return vm;
    }

    [Fact]
    public void AHeadWithNoCameraDoesNotOfferToScan()
    {
        var vm = Connecting();

        // Hidden rather than absent: the desktop head shares this markup and has no camera, and a
        // button that opens nothing is worse than no button.
        Assert.Contains("hidden", vm.Model.ScanButtonClass);
    }

    [Fact]
    public void AHeadThatWiredACameraOffersIt()
    {
        var vm = Connecting();

        vm.EnableScan();

        Assert.DoesNotContain("hidden", vm.Model.ScanButtonClass);
    }

    [Fact]
    public async Task WhatWasScannedGoesIntoTheServerFieldAndNoFurther()
    {
        var vm = Connecting();
        vm.EnableScan();
        var app = new BanterChatApp(vm)
        {
            ScanServerAsync = () => Task.FromResult<string?>("  cuprinet://intone/abc  "),
        };

        await app.ScanAsync();
        vm.ApplyPending();

        // Trimmed, shown, and nothing else: a link that arrived by camera is the one value on this
        // screen nobody can check by reading it back, so a person sees it before it is used.
        Assert.Equal("cuprinet://intone/abc", vm.Model.ConnectServer);
        Assert.NotEqual("Connecting", vm.Model.ConnectButtonText);
    }

    [Fact]
    public async Task BackingOutOfTheScannerChangesNothing()
    {
        var vm = Connecting();
        vm.Model.ConnectServer = "tcp://kept:7770";
        var app = new BanterChatApp(vm)
        {
            ScanServerAsync = () => Task.FromResult<string?>(null),
        };

        await app.ScanAsync();
        vm.ApplyPending();

        Assert.Equal("tcp://kept:7770", vm.Model.ConnectServer);
    }

    /// <summary>
    /// A head with no camera draws no button, rather than a dead one somebody can press.
    ///
    /// <para>Checked by measuring rather than by reading the class, because in this engine a
    /// <c>hidden</c> control stays in the tree at no size — the same reason the admin-only rail
    /// buttons are still there for everybody else. Size is the thing a person can see.</para>
    /// </summary>
    [Fact]
    public void WithNoCameraTheControlTakesNoSpace()
    {
        Assert.Equal(0, ScanButtonHeight(enableScan: false));
        Assert.True(ScanButtonHeight(enableScan: true) > 0, "enabling the scanner drew nothing");
    }

    private static float ScanButtonHeight(bool enableScan)
    {
        var vm = Connecting();
        if (enableScan)
        {
            vm.EnableScan();
        }

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.Refresh();

        var presentation = BanterChatApp.Presentation(412, 915);
        doc.BuildFrame(presentation.LogicalWidth, presentation.LogicalHeight);

        return Height(doc.Root);

        float Height(RenderNode node)
        {
            if (node.Element?.GetAttribute("class")?.Split(' ').Contains("connect-scan") == true)
            {
                return node.Height;
            }

            foreach (var child in node.Children)
            {
                var found = Height(child);
                if (found > 0)
                {
                    return found;
                }
            }

            return 0;
        }
    }
}
