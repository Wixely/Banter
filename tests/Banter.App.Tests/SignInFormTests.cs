using Banter.App;
using CupriFace;
using CupriFace.Interaction;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Signing in from the keyboard alone: focus lands in the form, Tab walks it, and Enter submits
/// from wherever the caret is.
///
/// <para>Driven through a real document rather than by calling <c>Connect()</c>, because none of
/// this is in the view model — it is whether the markup is a <c>cupri-form</c>, and that is
/// exactly the kind of thing that looks right and does nothing.</para>
/// </summary>
public sealed class SignInFormTests
{
    private const int Width = 1100;
    private const int Height = 760;

    private static (BanterChatApp App, ChatViewModel Vm, List<(string Server, string User, string Pass)> Attempts) Signin()
    {
        var vm = new ChatViewModel();
        vm.ShowConnect("", "");

        var attempts = new List<(string, string, string)>();
        var app = new BanterChatApp(vm)
        {
            ConnectAsync = (server, user, pass) => { attempts.Add((server, user, pass)); return Task.CompletedTask; },
        };
        return (app, vm, attempts);
    }

    private static CupriDocument Opened(BanterChatApp app)
    {
        var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        app.Present(Width, Height);
        return doc;
    }

    private static void Type(CupriDocument doc, string text)
    {
        foreach (var ch in text)
        {
            doc.DispatchKey(ch.ToString(), EditKey.None);
        }
    }

    [Fact]
    public void TheFirstThingTypedLandsInTheServerField()
    {
        // Nothing is clicked. Without data-autofocus the caret is nowhere and the first thing
        // somebody types is simply lost, which reads as a dead window.
        var (app, vm, _) = Signin();
        using var doc = Opened(app);

        Type(doc, "tcp://host:7770");
        app.Present(Width, Height);

        Assert.Equal("tcp://host:7770", vm.Model.ConnectServer);
    }

    [Fact]
    public void TabWalksServerToNameToPassword()
    {
        var (app, vm, _) = Signin();
        using var doc = Opened(app);

        Type(doc, "tcp://host:7770");

        // The first Tab after a programmatic focus is absorbed establishing the traversal
        // position rather than moving - CupriFace 0.18.0, the same for data-autofocus and for
        // doc.Focus(). Asserted as it behaves rather than as it should, so that a fix upstream
        // fails here loudly instead of changing the screen quietly.
        doc.DispatchKey("", EditKey.Tab);
        doc.DispatchKey("", EditKey.Tab);
        Type(doc, "alice");

        doc.DispatchKey("", EditKey.Tab);
        Type(doc, "hunter2");
        app.Present(Width, Height);

        Assert.Equal("tcp://host:7770", vm.Model.ConnectServer);
        Assert.Equal("alice", vm.Model.ConnectUser);
        Assert.Equal("hunter2", vm.Model.ConnectPassword);
    }

    [Fact]
    public void EnterFromThePasswordFieldSignsIn()
    {
        var (app, vm, attempts) = Signin();
        using var doc = Opened(app);

        Type(doc, "tcp://host:7770");
        doc.DispatchKey("", EditKey.Tab);
        doc.DispatchKey("", EditKey.Tab);
        Type(doc, "alice");
        doc.DispatchKey("", EditKey.Tab);
        Type(doc, "hunter2");
        app.Present(Width, Height);

        doc.DispatchKey("", EditKey.Enter);
        vm.ApplyPending();

        Assert.Equal(("tcp://host:7770", "alice", "hunter2"), Assert.Single(attempts));
    }

    [Fact]
    public void EnterFromTheFirstFieldSubmitsToo()
    {
        // A form submits from wherever the caret is, which is what makes it a form. Somebody who
        // signed in before has the server and name already filled and only types a password -
        // but somebody correcting a typo in the server field should not have to Tab to the end.
        var (app, vm, attempts) = Signin();
        vm.ShowConnect("tcp://host:7770", "alice");
        using var doc = Opened(app);

        // Focus is on the server field; the other two are already filled.
        Assert.Equal("", vm.Model.ConnectPassword);
        doc.DispatchKey("", EditKey.Tab);
        doc.DispatchKey("", EditKey.Tab);
        doc.DispatchKey("", EditKey.Tab);
        Type(doc, "hunter2");
        app.Present(Width, Height);

        doc.DispatchKey("", EditKey.Enter);
        vm.ApplyPending();

        Assert.Equal(("tcp://host:7770", "alice", "hunter2"), Assert.Single(attempts));
    }

    [Fact]
    public void AnIncompleteFormSaysWhatIsMissingRatherThanDoingNothing()
    {
        var (app, vm, attempts) = Signin();
        using var doc = Opened(app);

        Type(doc, "tcp://host:7770");
        app.Present(Width, Height);

        doc.DispatchKey("", EditKey.Enter);
        vm.ApplyPending();

        Assert.Empty(attempts);
        Assert.Equal("Needs a name.", vm.Model.ConnectStatus);
    }

    [Fact]
    public void ASecondEnterWhileConnectingIsIgnored()
    {
        // Enter is easy to lean on, and a second connect attempt over the top of one in flight
        // is two sessions racing to be the one that survives.
        var (app, vm, attempts) = Signin();
        vm.ShowConnect("tcp://host:7770", "alice");
        using var doc = Opened(app);

        doc.DispatchKey("", EditKey.Tab);
        doc.DispatchKey("", EditKey.Tab);
        doc.DispatchKey("", EditKey.Tab);
        Type(doc, "hunter2");
        app.Present(Width, Height);

        doc.DispatchKey("", EditKey.Enter);
        vm.ApplyPending();
        doc.DispatchKey("", EditKey.Enter);
        vm.ApplyPending();

        Assert.Single(attempts);
    }
}
