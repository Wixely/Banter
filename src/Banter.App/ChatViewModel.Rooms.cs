using Banter.Protocol;

namespace Banter.App;

/// <summary>
/// Where each room sits, and who may draw on it from elsewhere: the room list drawn as the tree
/// it is, and a panel that says - and for an admin, changes - both.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Each room's disclosure, as the server last listed it.</summary>
    private readonly Dictionary<string, RoomDisclosure> _disclosures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each room's parley, as the server last said.</summary>
    private readonly Dictionary<string, RoomParley> _parleys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The listing with each room's disclosure, which the tree and the panel both need.</summary>
    public void SetRoomListing(IEnumerable<(string Name, string? Parent, int Members, RoomDisclosure Disclosure)> rooms) =>
        SetRoomListing(rooms.Select(r => (r.Name, r.Parent, r.Members, r.Disclosure, RoomParley.Pairs)));

    /// <summary>The listing with each room's disclosure and parley.</summary>
    public void SetRoomListing(IEnumerable<(string Name, string? Parent, int Members, RoomDisclosure Disclosure, RoomParley Parley)> rooms)
    {
        var all = rooms.ToList();
        foreach (var room in all)
        {
            _disclosures[room.Name] = room.Disclosure;
            _parleys[room.Name] = room.Parley;
        }

        SetRoomListing(all.Select(r => (r.Name, r.Parent, r.Members)));
        RefreshRoomPanel();
    }

    /// <summary>One room's disclosure changed, said by the server.</summary>
    public void SetRoomDisclosure(string room, RoomDisclosure disclosure)
    {
        _disclosures[room] = disclosure;
        RelabelJoinedRooms();
        RefreshRoomPanel();
    }

    /// <summary>One room's parley changed, or was said on joining.</summary>
    public void SetRoomParley(string room, RoomParley parley)
    {
        _parleys[room] = parley;
        RefreshRoomPanel();
    }

    public RoomDisclosure DisclosureOf(string room) => _disclosures.GetValueOrDefault(room, RoomDisclosure.Members);

    public RoomParley ParleyOf(string room) => _parleys.GetValueOrDefault(room, RoomParley.Pairs);

    /// <summary>Each room's sensitivity, as the server last said. Sensitive until it says.</summary>
    private readonly Dictionary<string, DataSensitivity> _sensitivities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The listing's sensitivities, beside <see cref="SetRoomListing"/>.</summary>
    public void SetRoomSensitivities(IEnumerable<(string Room, DataSensitivity Sensitivity)> rooms)
    {
        foreach (var (room, sensitivity) in rooms)
        {
            _sensitivities[room] = sensitivity;
        }

        RefreshRoomPanel();
    }

    /// <summary>One room's sensitivity changed, said by the server.</summary>
    public void SetRoomSensitivity(string room, DataSensitivity sensitivity)
    {
        _sensitivities[room] = sensitivity;
        RefreshRoomPanel();
    }

    public DataSensitivity SensitivityOf(string room) => _sensitivities.GetValueOrDefault(room, DataSensitivity.Sensitive);

    /// <summary>How deep a room sits. Bounded, so a tree that is somehow cyclic still draws.</summary>
    private int DepthOf(string room)
    {
        var depth = 0;
        for (var at = _roomParents.GetValueOrDefault(room); at is not null && depth < 16; at = _roomParents.GetValueOrDefault(at))
        {
            depth++;
        }

        return depth;
    }

    /// <summary>A room's place in the tree as a sortable key: its ancestors' names, root first.</summary>
    private string TreeKey(string room)
    {
        var path = new List<string> { room };
        for (var at = _roomParents.GetValueOrDefault(room); at is not null && path.Count < 16; at = _roomParents.GetValueOrDefault(at))
        {
            path.Insert(0, at);
        }

        return string.Join('\u0001', path).ToLowerInvariant();
    }

    /// <summary>Whether <paramref name="room"/> sits anywhere beneath <paramref name="ancestor"/>.</summary>
    private bool IsBeneath(string room, string ancestor)
    {
        var steps = 0;
        for (var at = _roomParents.GetValueOrDefault(room); at is not null && steps < 16; at = _roomParents.GetValueOrDefault(at), steps++)
        {
            if (string.Equals(at, ancestor, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ── The room panel ───────────────────────────────────────────────────────────────────────

    public bool RoomPanelOpen => !Model.RoomPanelClass.Contains("hidden", StringComparison.Ordinal);

    public void ShowRoomPanel(bool show)
    {
        Model.RoomPanelClass = show && Model.ActiveRoom.Length > 0 ? "mgmt room-overlay" : "mgmt hidden";
        RefreshRoomPanel();
    }

    /// <summary>
    /// Fills the panel for the room on screen: where it sits, and who may draw on it. An admin
    /// gets both as choices; everybody else reads them, because a person who cannot change
    /// these still deserves to know what the room they are typing into will let out.
    /// </summary>
    private void RefreshRoomPanel()
    {
        var room = Model.ActiveRoom;
        if (room.Length == 0)
        {
            return;
        }

        var parent = _roomParents.GetValueOrDefault(room) ?? "";
        var disclosure = DisclosureOf(room);
        var parley = ParleyOf(room);

        Model.RoomPanelTitle = room;
        Model.RoomPanelPlace = parent.Length > 0 ? $"Inside {parent}" : "At the top level";
        Model.RoomPanelEditClass = IsAdmin ? "room-edit" : "room-edit hidden";
        Model.RoomPanelReadClass = IsAdmin ? "room-read hidden" : "room-read";
        Model.RoomPanelDisclosure = DisclosureLabel(disclosure) + " - " + DisclosureHint(disclosure);
        Model.RoomPanelParley = "Agents: " + ParleyLabel(parley) + " - " + ParleyHint(parley);
        Model.ParleyChoices = Choices(parley.ToString().ToLowerInvariant(),
            [.. Enum.GetValues<RoomParley>().Select(p => (p.ToString().ToLowerInvariant(), ParleyLabel(p), ParleyHint(p)))]);

        var sensitivity = SensitivityOf(room);
        Model.RoomPanelSensitivity = SensitivityLabel(sensitivity) + " - " + SensitivityHint(sensitivity);
        Model.SensitivityChoices = Choices(sensitivity.ToString().ToLowerInvariant(),
            [.. new[] { DataSensitivity.Public, DataSensitivity.Internal, DataSensitivity.Sensitive }
                .Select(s => (s.ToString().ToLowerInvariant(), SensitivityLabel(s), SensitivityHint(s)))]);

        // Anywhere but inside itself.
        var places = _roomParents.Keys.Concat(Model.Rooms.Select(r => r.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !string.Equals(r, room, StringComparison.OrdinalIgnoreCase) && !IsBeneath(r, room))
            .OrderBy(TreeKey, StringComparer.Ordinal)
            .Select(r => (Value: r, Label: Indent(r) + r, Hint: ""));
        Model.ParentChoices = Choices(parent, [("", "Top level", "Not inside any room."), .. places]);

        Model.DisclosureChoices = Choices(disclosure.ToString().ToLowerInvariant(),
            [.. Enum.GetValues<RoomDisclosure>().Select(d => (d.ToString().ToLowerInvariant(), DisclosureLabel(d), DisclosureHint(d)))]);
        foreach (var choice in Model.DisclosureChoices.Where(c => c.Value == "global"))
        {
            choice.RowClass += " warn";
        }

        Model.DisclosureWarningClass = disclosure == RoomDisclosure.Global ? "room-warning" : "room-warning hidden";
    }

    /// <summary>Non-breaking, for the same reason the room list's labels are.</summary>
    private string Indent(string room) => new(' ', DepthOf(room) * 3);

    public static string DisclosureLabel(RoomDisclosure d) => d switch
    {
        RoomDisclosure.Isolated => "Isolated",
        RoomDisclosure.Children => "Children",
        RoomDisclosure.Global => "Global",
        _ => "Members",
    };

    public static string SensitivityLabel(DataSensitivity s) => s switch
    {
        DataSensitivity.Public => "Public",
        DataSensitivity.Internal => "Internal",
        _ => "Sensitive",
    };

    public static string SensitivityHint(DataSensitivity s) => s switch
    {
        DataSensitivity.Public => "Only what could be posted anywhere is said here. Any agent may be brought in.",
        DataSensitivity.Internal => "Ordinary work, not for outside eyes. Agents cleared for internal may be brought in.",
        _ => "Anything may be said here. Only agents cleared for sensitive may be brought in - or agents already in the room this one was opened from.",
    };

    public static string ParleyLabel(RoomParley p) => p switch
    {
        RoomParley.None => "None",
        RoomParley.Open => "Open",
        _ => "Pairs",
    };

    public static string ParleyHint(RoomParley p) => p switch
    {
        RoomParley.None => "The delegator answers people only, never another agent.",
        RoomParley.Open => "The delegator answers any agent that @-names it, however many agents are here.",
        _ => "The delegator answers an agent that @-names it while they are the only two agents here.",
    };

    public static string DisclosureHint(RoomDisclosure d) => d switch
    {
        RoomDisclosure.Isolated => "Nothing said here can be drawn on from any other room - not even by the agents in it.",
        RoomDisclosure.Children => "Rooms beneath this one may draw on what is said here.",
        RoomDisclosure.Global => "Any agent may draw on what is said here, from any room, whether or not it is in this one.",
        _ => "The agents in this room may draw on it from the other rooms they are in.",
    };
}
