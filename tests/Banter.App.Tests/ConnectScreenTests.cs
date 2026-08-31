using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The connect screen. A desktop head is handed its server and account on the command line and
/// never shows this; a phone has no command line, which is why it exists and why it lives in the
/// shared app rather than in the Android head, where none of this could be tested.
/// </summary>
public sealed class ConnectScreenTests
{
    private static ChatViewModel Showing(string server = "tcp://10.0.0.4:7770", string user = "alice")
    {
        var vm = new ChatViewModel();
        vm.ShowConnect(server, user);
        return vm;
    }

    [Fact]
    public void ItIsHiddenUntilAHeadAsksForIt()
    {
        var vm = new ChatViewModel();

        Assert.False(vm.ConnectVisible);

        vm.ShowConnect("tcp://host:7770", "alice");
        Assert.True(vm.ConnectVisible);
    }

    [Fact]
    public void WhatWasRememberedIsPreFilledButNeverThePassword()
    {
        var vm = Showing();

        Assert.Equal("tcp://10.0.0.4:7770", vm.Model.ConnectServer);
        Assert.Equal("alice", vm.Model.ConnectUser);

        // Nothing stores it, so it is asked for every time.
        Assert.Equal("", vm.Model.ConnectPassword);
    }

    [Fact]
    public void AFilledFormIsReadBack()
    {
        var vm = Showing();
        vm.Model.ConnectPassword = "hunter2";

        Assert.True(vm.TryReadConnect(out var server, out var user, out var password));
        Assert.Equal("tcp://10.0.0.4:7770", server);
        Assert.Equal("alice", user);
        Assert.Equal("hunter2", password);
    }

    [Fact]
    public void SurroundingSpaceIsTrimmedFromTheServerAndName()
    {
        // A pasted address or a phone keyboard's autocorrect both bring a trailing space.
        var vm = Showing("  tcp://10.0.0.4:7770  ", "  alice ");
        vm.Model.ConnectPassword = "pw";

        Assert.True(vm.TryReadConnect(out var server, out var user, out _));
        Assert.Equal("tcp://10.0.0.4:7770", server);
        Assert.Equal("alice", user);
    }

    [Theory]
    [InlineData("", "alice", "pw", "server")]
    [InlineData("tcp://host:7770", "", "pw", "name")]
    [InlineData("tcp://host:7770", "alice", "", "password")]
    public void AnIncompleteFormSaysWhatIsMissing(string server, string user, string password, string expected)
    {
        var vm = Showing(server, user);
        vm.Model.ConnectPassword = password;

        // A form that simply does nothing when tapped reads as broken.
        Assert.False(vm.TryReadConnect(out _, out _, out _));
        Assert.Contains(expected, vm.Model.ConnectStatus);
    }

    [Fact]
    public void SomethingThatIsNotAnAddressIsRefusedByName()
    {
        var vm = Showing("my server");
        vm.Model.ConnectPassword = "pw";

        Assert.False(vm.TryReadConnect(out _, out _, out _));
        Assert.Contains("my server", vm.Model.ConnectStatus);
    }

    [Fact]
    public void AFailedAttemptKeepsTheScreenAndClearsThePassword()
    {
        var vm = Showing();
        vm.Model.ConnectPassword = "wrong";
        vm.Connecting();

        vm.ConnectFailed("Refused: bad credentials");

        Assert.True(vm.ConnectVisible);
        Assert.Contains("bad credentials", vm.Model.ConnectStatus);

        // The likeliest reason is that it was wrong, and a stale one invites the same failure.
        Assert.Equal("", vm.Model.ConnectPassword);
        Assert.Equal("Connect", vm.Model.ConnectButtonText);
    }

    [Fact]
    public void SucceedingTakesTheScreenAndTheSecretAway()
    {
        var vm = Showing();
        vm.Model.ConnectPassword = "hunter2";

        vm.Connected();

        Assert.False(vm.ConnectVisible);
        Assert.Equal("", vm.Model.ConnectPassword);
    }

    [Fact]
    public void TheButtonSaysWhenAnAttemptIsRunning()
    {
        var vm = Showing();

        vm.Connecting();

        Assert.Equal("Connecting", vm.Model.ConnectButtonText);
        Assert.Contains("Connecting", vm.Model.ConnectStatus);
    }

    /// <summary>
    /// A head can learn a server address after the screen is already up — a browser fetching a
    /// link a node publishes, for instance, where the page regularly loads before the node has
    /// written one. That is the whole reason this exists.
    /// </summary>
    [Fact]
    public void AServerLearnedLateFillsAnEmptyField()
    {
        var vm = Showing(server: "", user: "alice");

        vm.SuggestConnectServer("cuprinet://intone/abc");

        Assert.Equal("cuprinet://intone/abc", vm.Model.ConnectServer);
    }

    [Fact]
    public void ItNeverOverwritesWhatSomeoneTyped()
    {
        var vm = Showing(server: "", user: "alice");
        vm.Model.ConnectServer = "tcp://the-one-i-want:7770";

        vm.SuggestConnectServer("cuprinet://intone/abc");

        // A field that rewrites itself under the person filling it in is worse than one that stays
        // empty, and this arrives on a timer they cannot see.
        Assert.Equal("tcp://the-one-i-want:7770", vm.Model.ConnectServer);
    }

    [Fact]
    public void ItStopsOnceTheScreenIsGone()
    {
        var vm = Showing(server: "", user: "alice");
        vm.Connected();

        vm.SuggestConnectServer("cuprinet://intone/abc");

        Assert.Equal("", vm.Model.ConnectServer);
    }

    [Fact]
    public void AnEmptySuggestionChangesNothing()
    {
        var vm = Showing(server: "tcp://host:7770", user: "alice");

        vm.SuggestConnectServer("");

        Assert.Equal("tcp://host:7770", vm.Model.ConnectServer);
    }


    /// <summary>
    /// The card is centred horizontally. It was not: the engine does not honour `margin: auto` on
    /// a flex item (CupriFace#76), so the card sat against the container's left padding on every
    /// head — measured at x=60 in a 1440-wide viewport, where centred is x=540.
    /// </summary>
    [Fact]
    public void TheCardIsCentred()
    {
        const int width = 1440, height = 900;
        var vm = new ChatViewModel();
        vm.ShowConnect("tcp://host:7770", "alice");
        var app = new BanterChatApp(vm);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(width, height);
        var pixels = doc.RenderToPixels(width, height, SkiaSharp.SKColors.Black);

        // Scan a row through the card and find where its fill (#151920) starts and ends against
        // the darker backdrop behind it.
        const int row = 250;
        int first = -1, last = -1;
        for (var x = 0; x < width; x++)
        {
            var i = ((row * width) + x) * 4;
            if (pixels[i] == 0x15 && pixels[i + 1] == 0x19 && pixels[i + 2] == 0x20)
            {
                if (first < 0)
                {
                    first = x;
                }

                last = x;
            }
        }

        Assert.True(first >= 0, "the card should be painted at all");

        // Its midpoint is the viewport's, within a pixel of rounding.
        var midpoint = (first + last) / 2.0;
        Assert.InRange(midpoint, (width / 2.0) - 2, (width / 2.0) + 2);
    }

    /// <summary>
    /// Nothing of the room shows beside it. The overlay is absolutely positioned but was declared
    /// before the agent roster, and the engine paints in document order — so the roster sat on top
    /// of a screen whose whole job is to be the only thing there.
    /// </summary>
    [Fact]
    public void NothingShowsBesideIt()
    {
        const int width = 1440, height = 900;
        var vm = new ChatViewModel();
        vm.ShowConnect("tcp://host:7770", "alice");
        var app = new BanterChatApp(vm);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(width, height);
        var pixels = doc.RenderToPixels(width, height, SkiaSharp.SKColors.Black);

        // A row well below the card, where only the backdrop should be.
        const int row = 700;
        for (var x = 0; x < width; x++)
        {
            var i = ((row * width) + x) * 4;
            Assert.True(
                pixels[i] == 0x0b && pixels[i + 1] == 0x0d && pixels[i + 2] == 0x10,
                $"x={x} shows #{pixels[i]:x2}{pixels[i + 1]:x2}{pixels[i + 2]:x2} through the connect screen");
        }
    }

}

/// <summary>The screen driven through a real document, the way a thumb hits it.</summary>
public sealed class ConnectScreenAppTests
{
    [Fact]
    public void TappingConnectHandsTheFormToTheHead()
    {
        var vm = new ChatViewModel();
        vm.ShowConnect("tcp://10.0.0.4:7770", "alice");
        vm.Model.ConnectPassword = "hunter2";

        var attempts = new List<(string Server, string User, string Password)>();
        var app = new BanterChatApp(vm)
        {
            ConnectAsync = (s, u, p) => { attempts.Add((s, u, p)); return Task.CompletedTask; },
        };

        app.Connect();
        vm.ApplyPending();

        Assert.Equal(("tcp://10.0.0.4:7770", "alice", "hunter2"), Assert.Single(attempts));
    }

    [Fact]
    public void TappingTwiceDoesNotConnectTwice()
    {
        var vm = new ChatViewModel();
        vm.ShowConnect("tcp://host:7770", "alice");
        vm.Model.ConnectPassword = "pw";

        var attempts = 0;
        var app = new BanterChatApp(vm) { ConnectAsync = (_, _, _) => { attempts++; return Task.CompletedTask; } };

        app.Connect();
        vm.ApplyPending();

        // An attempt is already running; a second tap is a slip, not a second account.
        app.Connect();
        vm.ApplyPending();

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void AnIncompleteFormNeverReachesTheHead()
    {
        var vm = new ChatViewModel();
        vm.ShowConnect("tcp://host:7770", "alice");        // no password

        var attempts = 0;
        var app = new BanterChatApp(vm) { ConnectAsync = (_, _, _) => { attempts++; return Task.CompletedTask; } };

        app.Connect();
        vm.ApplyPending();

        Assert.Equal(0, attempts);
        Assert.Contains("password", vm.Model.ConnectStatus);
    }

    [Fact]
    public void TheAppLaysOutWithTheConnectScreenUp()
    {
        var vm = new ChatViewModel();
        vm.ShowConnect("tcp://host:7770", "alice");
        var app = new BanterChatApp(vm);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(420, 900);                     // a phone, not a desktop
        var pixels = doc.RenderToPixels(420, 900, SkiaSharp.SKColors.Black);

        Assert.Equal(420 * 900 * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
    }
}
