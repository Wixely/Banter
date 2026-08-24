using CupriFace;
using CupriFace.Binding;

namespace Banter.App.Spikes;

/// <summary>One rendered chat line. Fixed-height rows are what <c>cupri-virtual</c> requires.</summary>
[CupriBindable]
public sealed partial class TimelineRow
{
    public string Sender { get; set; } = "";
    public string Text { get; set; } = "";
}

[CupriBindable]
public sealed partial class TimelineModel
{
    public string Room { get; set; } = "#main";
    public string Composer { get; set; } = "";
    public List<TimelineRow> Messages { get; set; } = [];
}

/// <summary>
/// The virtualized timeline: <c>cupri-virtual</c> windows the message list to a screenful, so
/// render cost should not grow with history size. Fixed row height is the constraint it imposes.
/// </summary>
public sealed class VirtualTimelineApp(TimelineModel model) : CupriApp
{
    public override string Title => "Banter (virtual timeline spike)";
    public override object Model => model;

    public override string Html => """
        <div class="app">
          <div class="room">{{Room}}</div>
          <cupri-virtual class="timeline" height="560" item-height="28">
            <div class="line" data-repeat="Messages"><b>{{Sender}}</b> {{Text}}</div>
          </cupri-virtual>
          <cupri-textarea class="composer" value="{{Composer}}" placeholder="Message"></cupri-textarea>
        </div>
        """;

    public override string Css => """
        .app { display: flex; flex-direction: column; height: 720px; font-size: 14px; }
        .room { padding: 8px; font-weight: bold; }
        .timeline { flex: 1; }
        .line { height: 28px; padding: 4px 8px; overflow: hidden; }
        .composer { min-height: 60px; max-height: 120px; }
        """;
}

/// <summary>
/// The same timeline without virtualization — every message is a laid-out box, and rows may be
/// any height (what real chat messages are). The control case for the scrollback spike.
/// </summary>
public sealed class PlainTimelineApp(TimelineModel model) : CupriApp
{
    public override string Title => "Banter (plain timeline spike)";
    public override object Model => model;

    public override string Html => """
        <div class="app">
          <div class="room">{{Room}}</div>
          <div class="timeline">
            <div class="line" data-repeat="Messages"><b>{{Sender}}</b> {{Text}}</div>
          </div>
          <cupri-textarea class="composer" value="{{Composer}}" placeholder="Message"></cupri-textarea>
        </div>
        """;

    public override string Css => """
        .app { display: flex; flex-direction: column; height: 720px; font-size: 14px; }
        .room { padding: 8px; font-weight: bold; }
        .timeline { flex: 1; overflow: scroll; }
        .line { padding: 4px 8px; }
        .composer { min-height: 60px; max-height: 120px; }
        """;
}
