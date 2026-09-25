using Banter.App;
using Banter.Voice;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The composer's three buttons - microphone, Attach and Send - which carry glyphs rather than
/// words.
///
/// <para>The word WAS the accessible name. Replacing it with a drawn icon leaves the
/// accessibility tree nothing to name the control with, and a screen reader reaching the end of
/// the composer finds three buttons called nothing - on every head, since the ARIA mirror is the
/// engine's, not a per-platform thing. These pin the labels that replaced the words.</para>
/// </summary>
public sealed class ComposerButtonTests
{
    private const int Width = 1100;
    private const int Height = 760;

    private static string AriaOfChat(Action<ChatViewModel>? also = null)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.SetStatus("Connected", connected: true);
        vm.Connected("tcp://127.0.0.1:7770", "alice");
        vm.EnableAttach();
        also?.Invoke(vm);
        vm.ApplyPending();

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        app.Present(Width, Height);

        return doc.BuildAriaHtml(Width, Height);
    }

    [Fact]
    public void SendIsStillNamed()
    {
        Assert.Contains("aria-label=\"Send\"", AriaOfChat());
    }

    /// <summary>"Attach a file", not "Attach": a label is read on its own, with none of the
    /// surrounding screen to say what is being attached to what.</summary>
    [Fact]
    public void AttachIsStillNamed()
    {
        Assert.Contains("aria-label=\"Attach a file\"", AriaOfChat());
    }

    /// <summary>
    /// The microphone's name is bound rather than fixed, because the button is a toggle: the same
    /// control opens the gate and closes it, and a label saying "Talk" while the gate is open is
    /// worse than no label - it names the state the button is about to leave.
    /// </summary>
    [Fact]
    public void TheMicIsNamedForWhatItWillDo()
    {
        Assert.Contains("aria-label=\"Talk\"", AriaOfChat(vm => vm.EnableVoice(readbackAvailable: true)));

        Assert.Contains("aria-label=\"Stop\"", AriaOfChat(vm =>
        {
            vm.EnableVoice(readbackAvailable: true);
            vm.SetVoiceState(VoiceSessionState.Listening);
        }));
    }

    /// <summary>
    /// All three composer buttons share one box. Left to their own contents they do not: three
    /// buttons sized by three glyphs come out a pixel or two apart, and the row's align-items
    /// then centres three different heights on one line rather than lining their edges up.
    /// </summary>
    [Fact]
    public void TheButtonsAreTheSameSize()
    {
        var app = new BanterChatApp(new ChatViewModel());

        foreach (var selector in new[] { ".mic {", ".attach-open {", ".send {" })
        {
            var at = app.Css.IndexOf(selector, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{selector} is no longer in the stylesheet");

            var rule = app.Css[at..app.Css.IndexOf('}', at)];
            Assert.Contains("width: 40px", rule);
            Assert.Contains("height: 30px", rule);
        }
    }
}
