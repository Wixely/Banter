using CupriFace;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Engine behaviour this application is shaped around, measured rather than remembered.
///
/// <para>These exist because a workaround outlives its reason quietly: the comment that prompted
/// this file cited CupriFace 0.18.0 and was still being obeyed six versions later. A test says
/// what is true today and fails the day it stops being true.</para>
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
    /// A plain <c>overflow: scroll</c> box takes the wheel, both ways, and stops at its ends.
    ///
    /// <para>Banter's settings page is built as though it does not: the card is a fixed 620px
    /// because "the list below does not scroll", and the fields are split into sections to fit
    /// inside that. On 0.34.0 that is simply not so — which makes the ceiling removable, and is
    /// the thing this test is here to keep honest.</para>
    /// </summary>
    [Fact]
    public void AnOverflowBoxTakesTheWheel()
    {
        using var doc = new OverflowBox().CreateDocument();
        doc.BuildFrame(400, 300);

        var box = doc.Root.Children[0];
        Assert.True(box.IsScrollable, "the box should overflow: 9 rows of 40 in 100px");

        // Down from the top. A wheel UP here moves nothing and is answered false — correctly, as a
        // browser does, and the reason this was once measured as "the engine ignores the wheel".
        Assert.False(doc.DispatchWheel(50, 50, -120), "already at the top");

        Assert.True(doc.DispatchWheel(50, 50, 120), "a wheel down inside a scrollable box");
        doc.BuildFrame(400, 300);
        Assert.Equal(120, doc.Root.Children[0].ScrollY, 1);

        Assert.True(doc.DispatchWheel(50, 50, -120), "and back up again");
        doc.BuildFrame(400, 300);
        Assert.Equal(0, doc.Root.Children[0].ScrollY, 1);

        output.WriteLine("a plain overflow box scrolls on CupriFace 0.34.0");
    }
}
