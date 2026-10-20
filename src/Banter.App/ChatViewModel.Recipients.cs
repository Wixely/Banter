namespace Banter.App;

/// <summary>
/// Who acts on what you say in a room. By default the room's delegator does, and decides from
/// there; ticking agents in the roster replaces that, per room, with exactly the agents ticked.
/// The choice goes on each message (<c>MsgPayload.To</c>), so it is the agents that honour it and
/// the server that refuses to let an agent make the same choice.
///
/// <para>And the warning that goes with it. A third-party agent that gets your words gets them on
/// somebody else's servers, so the composer says so before you send rather than the room saying
/// so after.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Rooms where a choice has been made, and who was chosen. A room absent here is on
    /// the default, which is not the same as an empty choice: empty means nobody acts unless
    /// named with an @.</summary>
    private readonly Dictionary<string, HashSet<string>> _recipients = new(StringComparer.OrdinalIgnoreCase);

    private bool _recipientsChanged;

    /// <summary>The list to send with a message in <paramref name="room"/>: null for the default,
    /// otherwise exactly the agents ticked there.</summary>
    public IReadOnlyList<string>? RecipientsFor(string room) =>
        _recipients.TryGetValue(room, out var chosen) ? [.. chosen.Order(StringComparer.OrdinalIgnoreCase)] : null;

    /// <summary>Every room's choice, for a head to persist.</summary>
    public IReadOnlyDictionary<string, string[]> SavedRecipients =>
        _recipients.ToDictionary(
            r => r.Key, r => r.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Restores what a head saved. A room with nothing saved stays on the default.</summary>
    public void LoadRecipients(IReadOnlyDictionary<string, string[]>? saved)
    {
        _recipients.Clear();
        foreach (var (room, nicks) in saved ?? new Dictionary<string, string[]>())
        {
            _recipients[room] = new HashSet<string>(nicks, StringComparer.OrdinalIgnoreCase);
        }

        RefreshPicks();
    }

    /// <summary>
    /// Ticks or unticks an agent in the room on screen. The first change starts from what the
    /// default would do - the delegator - so ticking a second agent adds it rather than quietly
    /// taking the delegator off.
    /// </summary>
    public void ToggleRecipient(string nick)
    {
        var room = Model.ActiveRoom;
        if (room.Length == 0 || nick.Length == 0)
        {
            return;
        }

        if (!_recipients.TryGetValue(room, out var chosen))
        {
            chosen = [.. DefaultRecipients(room)];
            _recipients[room] = chosen;
        }

        if (!chosen.Remove(nick))
        {
            chosen.Add(nick);
        }

        _recipientsChanged = true;
        RefreshPicks();
    }

    /// <summary>Back to the default for the room on screen: the delegator decides.</summary>
    public void ClearRecipients()
    {
        if (_recipients.Remove(Model.ActiveRoom))
        {
            _recipientsChanged = true;
            RefreshPicks();
        }
    }

    /// <summary>Whether the choice changed since a head last saved it; clears as it answers.</summary>
    public bool TakeRecipientsChanged()
    {
        var changed = _recipientsChanged;
        _recipientsChanged = false;
        return changed;
    }

    public bool HasRecipientChoice(string room) => _recipients.ContainsKey(room);

    private HashSet<string> DefaultRecipients(string room) =>
        _delegators.GetValueOrDefault(room) is { Length: > 0 } delegator
            ? new HashSet<string>([delegator], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private IReadOnlySet<string> EffectiveRecipients(string room) =>
        _recipients.TryGetValue(room, out var chosen) ? chosen : DefaultRecipients(room);

    /// <summary>Ticks the roster's boxes to match the room on screen, and offers the reset only
    /// where there is something to reset.</summary>
    private void RefreshPicks()
    {
        var room = Model.ActiveRoom;
        var ticked = EffectiveRecipients(room);
        foreach (var row in Model.Agents)
        {
            row.PickClass = ticked.Contains(row.Nick) ? "pick on" : "pick";
        }

        var custom = HasRecipientChoice(room);
        Model.PickClearClass = custom ? "pick-clear" : "pick-clear hidden";
        Model.PickHint = custom
            ? "Your messages go to the ticked agents."
            : "Your messages go to the delegator.";

        // What is ticked is half of what the warning is about.
        _egressKey = null;
    }

    private string? _egressKey;

    /// <summary>
    /// Says, in the composer, when what is being written would reach a third-party agent: ticked,
    /// named with an @, or the delegator itself when nothing is ticked. Recomputed only when the
    /// room, the text or the ticks change, and answers whether anything on screen did.
    /// </summary>
    public bool RefreshEgress()
    {
        var room = Model.ActiveRoom;
        var key = room + "\n" + Model.Composer;
        if (key == _egressKey)
        {
            return false;
        }

        _egressKey = key;
        var ticked = EffectiveRecipients(room);
        var third = Model.Agents
            .Where(a => a.Locality != "local")
            .Where(a => ticked.Contains(a.Nick) || NamesWithAt(Model.Composer, a.Nick))
            .Select(a => a.Nick)
            .ToList();

        var cls = third.Count > 0 ? "egress-warn" : "egress-warn hidden";
        var tip = third.Count > 0
            ? $"Goes to a third-party agent: {string.Join(", ", third)}. What you send leaves this server."
            : "";

        var changed = cls != Model.EgressClass || tip != Model.EgressTip;
        Model.EgressClass = cls;
        Model.EgressTip = tip;
        return changed;
    }

    /// <summary>An explicit <c>@nick</c>, ending where the nick does - the rule agents answer to.</summary>
    private static bool NamesWithAt(string text, string nick) => Banter.Client.Core.Mentions.Names(text, nick);
}
