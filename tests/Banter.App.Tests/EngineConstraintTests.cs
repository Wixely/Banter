using CupriFace;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Engine behaviour this application is shaped around.
///
/// <para>Each of these pins a limitation rather than a feature, and each is here so that the day
/// the engine stops having it, a test fails and says which piece of Banter can be deleted. The
/// alternative is a workaround that outlives its reason by years because nobody thought to
/// re-measure — which is how the comment that prompted this one came to cite CupriFace 0.18.0.</para>
/// </summary>
public sealed class EngineConstraintTests(ITestOutputHelper output)
{
    /// <summary>A box with more content than height, and nothing clever about it.</summary>
    private sealed class OverflowBox : CupriApp
    {
        public override string Html => """
            <div class="outer">
              <div class="row">one</div><div class="row">two</div><div class="row">three</div>
              <div class="row">four</div><div class="row">five</div><div class="row">six</div>
              <div class="row">seven</div><div class="row">eight</div><div class="row">nine</div>
            </div>
            """;

        public override string Css => """
            .outer { width: 200px; height: 100px; overflow: scroll; }
            .row { height: 40px; }
            """;
    }

    /// <summary>
    /// A plain <c>overflow: scroll</c> box ignores the wheel; only <c>cupri-virtual</c> scrolls.
    ///
    /// <para><b>When this test fails, the engine has fixed it.</b> Two things in the settings page
    /// exist only because of it and can then go: <c>.settings-card</c>'s fixed 620px height, which
    /// is currently a hard ceiling on how many settings can exist at all, and the section tabs that
    /// split the fields into card-sized groups.</para>
    ///
    /// <para>Measured on 0.18.0 when the ceiling was added, and again on 0.34.0 — unchanged.</para>
    /// </summary>
    [Fact]
    public void AnOverflowBoxStillIgnoresTheWheel()
    {
        using var doc = new OverflowBox().CreateDocument();
        doc.BuildFrame(400, 300);

        var handled = doc.DispatchWheel(50, 50, -120);

        output.WriteLine($"wheel over a plain overflow box: handled = {handled}");
        Assert.False(handled, "the engine now scrolls a plain overflow box — see this test's notes");
    }
}
