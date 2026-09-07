using Banter.App;
using Banter.Voice;
using CupriFace;
using CupriFace.Interaction;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The Talk button is held, not clicked.
///
/// <para>A click arrives only on release, so a click-toggled microphone cannot tell "still
/// talking" from "finished" — and the release, which is exactly when somebody stops speaking, is
/// not an event it can see. Driven through real pointer dispatch, because the whole question is
/// which phases the button is wired to.</para>
/// </summary>
public sealed class PushToTalkTests
{
    private const int Width = 1000;
    private const int Height = 700;

    private static (BanterChatApp App, ChatViewModel Vm, List<bool> Asked) Wired()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.EnableVoice(readbackAvailable: false);

        var asked = new List<bool>();
        var app = new BanterChatApp(vm)
        {
            // Echo what the head would do, so Listening tracks what was asked for.
            VoiceToggleAsync = open =>
            {
                asked.Add(open);
                vm.SetVoiceState(open ? VoiceSessionState.Capturing : VoiceSessionState.Idle);
                return Task.CompletedTask;
            },
        };
        return (app, vm, asked);
    }

    private static (CupriDocument Doc, float X, float Y) MicAt(BanterChatApp app)
    {
        var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        for (var y = (float)Height - 1; y > 0; y -= 2)
        {
            for (var x = 0f; x < Width; x += 4)
            {
                if (doc.HitTest(x, y)?.Element?.Closest("[data-mic]") is not null)
                {
                    return (doc, x, y);
                }
            }
        }

        throw new Xunit.Sdk.XunitException("nothing painted belongs to the Talk button");
    }

    [Fact]
    public void HoldingOpensTheMicrophoneAndReleasingClosesIt()
    {
        var (app, _, asked) = Wired();
        var (doc, x, y) = MicAt(app);
        using var _d = doc;

        doc.DispatchPointer(1, PointerPhase.Down, x, y);
        Assert.Equal([true], asked);

        doc.DispatchPointer(1, PointerPhase.Up, x, y);
        Assert.Equal([true, false], asked);
    }

    [Fact]
    public void HoldingStaysOpenWhileTheFingerMoves()
    {
        // A held button that closes the moment the pointer wanders a pixel is a button that
        // stops recording mid-sentence.
        var (app, vm, asked) = Wired();
        var (doc, x, y) = MicAt(app);
        using var _d = doc;

        doc.DispatchPointer(1, PointerPhase.Down, x, y);
        doc.DispatchPointer(1, PointerPhase.Move, x + 2, y);
        doc.DispatchPointer(1, PointerPhase.Move, x + 4, y + 1);

        Assert.Equal([true], asked);
        Assert.True(vm.Listening);
    }

    [Fact]
    public void ADraggedOffOrLostPointerStillClosesIt()
    {
        // Otherwise the microphone stays open with nothing on screen holding it down.
        var (app, vm, asked) = Wired();
        var (doc, x, y) = MicAt(app);
        using var _d = doc;

        doc.DispatchPointer(1, PointerPhase.Down, x, y);
        doc.DispatchPointer(1, PointerPhase.Cancel, x, y);

        Assert.Equal([true, false], asked);
        Assert.False(vm.Listening);
    }

    [Fact]
    public void ASecondReleaseDoesNotCloseAgain()
    {
        // A pointer can report Up twice, or Cancel after Up. Asking again would close a
        // recording somebody has since started with the hotkey.
        var (app, _, asked) = Wired();
        var (doc, x, y) = MicAt(app);
        using var _d = doc;

        doc.DispatchPointer(1, PointerPhase.Down, x, y);
        doc.DispatchPointer(1, PointerPhase.Up, x, y);
        doc.DispatchPointer(1, PointerPhase.Up, x, y);
        doc.DispatchPointer(1, PointerPhase.Cancel, x, y);

        Assert.Equal([true, false], asked);
    }

    [Fact]
    public void WithNoMicrophoneWiredNothingIsAsked()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        var asked = new List<bool>();
        var app = new BanterChatApp(vm) { VoiceToggleAsync = o => { asked.Add(o); return Task.CompletedTask; } };

        app.HoldVoice(true);
        app.HoldVoice(false);

        Assert.Empty(asked);
    }
}
