using Banter.Protocol;

namespace Banter.App;

/// <summary>
/// The work page: every room's tasks at once, for an operator rather than a participant.
///
/// <para>The roster already carries a Work strip — this room's tasks, title and state, glanceable
/// while you talk. That is the right thing for somebody in the room and the wrong thing for
/// somebody running the server, who needs the opposite: everything, across rooms, with the fields
/// that say whether work is actually moving. Who holds it, since when, whether the lease is about
/// to expire and hand it to somebody else, and what came back when it finished. So this is a page
/// rather than a bigger strip.</para>
///
/// <para>Admin-only, and it discloses nothing new: an admin is already added to every room an
/// agent opens (§8a), so this is one screen instead of visiting each board in turn.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    private IReadOnlyList<TaskInfoPayload> _taskListing = [];

    public void ShowWorkPanel(bool show)
    {
        Model.WorkPanelClass = show ? "mgmt" : "mgmt hidden";
        if (show)
        {
            Model.AgentsPanelClass = "mgmt hidden";
            Model.UsersPanelClass = "mgmt hidden";
            Model.SettingsPanelClass = "mgmt hidden";
        }

        ClearTaskDetail();
    }

    public bool WorkPanelOpen => !Model.WorkPanelClass.Contains("hidden", StringComparison.Ordinal);

    /// <summary>Whether finished work is listed too. Off by default: a board of things already
    /// done buries the two that are stuck.</summary>
    public bool IncludeFinished { get; private set; }

    public void SetTasks(IEnumerable<TaskInfoPayload> tasks)
    {
        _taskListing = [.. tasks];
        Model.AdminTasks = [.. _taskListing.Select(t => new AdminTaskRow
        {
            TaskId = t.TaskId,
            Title = t.Title,
            Detail = t.Assignee is { Length: > 0 } who
                ? $"{t.Room} · {Verb(t.State)} by {who}"
                : $"{t.Room} · {Verb(t.State)}",
            Initials = t.Assignee is { Length: > 0 } holder ? InitialsOf(holder) : "—",
            State = Describe(t),
            // Green for moving, amber for waiting, plain for done: the point of the list is to
            // find the one that has stopped.
            StateClass = t.State switch
            {
                TaskState.Open => "mgmt-state pending",
                TaskState.Done or TaskState.Failed => "mgmt-state muted",
                _ => "mgmt-state",
            },
            RowClass = string.Equals(t.TaskId, Model.TaskSelected, StringComparison.Ordinal)
                ? "mgmt-row selected"
                : "mgmt-row",
        })];

        Model.WorkStatus = Model.AdminTasks.Count switch
        {
            0 => IncludeFinished ? "No work at all" : "Nothing outstanding",
            1 => "1 task",
            var n => $"{n} tasks",
        };

        Model.TaskScopeChoices = ScopeChoices(IncludeFinished ? "all" : "live");

        // The page refreshes itself, so the open detail has to move with it: a lease counting
        // down behind a pane that still shows the old number is the one thing this page exists to
        // get right. Re-selecting also drops the pane when the task has gone.
        if (Model.TaskSelected.Length > 0)
        {
            SelectTask(Model.TaskSelected);
        }
    }

    public void SelectTask(string taskId)
    {
        var task = _taskListing.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal));
        if (task is null)
        {
            ClearTaskDetail();
            return;
        }

        Model.TaskSelected = task.TaskId;
        foreach (var row in Model.AdminTasks)
        {
            row.RowClass = string.Equals(row.TaskId, task.TaskId, StringComparison.Ordinal)
                ? "mgmt-row selected"
                : "mgmt-row";
        }

        Model.TaskDetailTitle = task.Title;
        Model.TaskDetailSubtitle = $"Posted by {task.Poster} in {task.Room}.";
        Model.TaskRoom = task.Room;
        Model.TaskState = Describe(task);
        Model.TaskPoster = task.Poster;
        Model.TaskAssignee = task.Assignee is { Length: > 0 } who ? who : "nobody yet";
        Model.TaskBody = task.Body.Length > 0 ? task.Body : "(no description)";
        Model.TaskId = task.TaskId;
        Model.TaskTimes = Times(task);
        Model.TaskLease = Lease(task);

        // Only when there is one: an empty result row on every open task is a row that says
        // nothing on most of the page.
        Model.TaskResult = task.Result ?? "";
        Model.TaskResultClass = task.Result is { Length: > 0 } ? "mgmt-field" : "mgmt-field hidden";

        Model.TaskDetailClass = "mgmt-detail";
        Model.TaskEmptyClass = "mgmt-empty hidden";
    }

    public void ClearTaskDetail()
    {
        Model.TaskSelected = "";
        foreach (var row in Model.AdminTasks)
        {
            row.RowClass = "mgmt-row";
        }

        Model.TaskDetailClass = "mgmt-detail hidden";
        Model.TaskEmptyClass = "mgmt-empty";
        Model.TaskDetailTitle = "";
        Model.TaskDetailSubtitle = "";
    }

    public void ChooseTaskScope(string value)
    {
        IncludeFinished = value == "all";
        Model.TaskScopeChoices = ScopeChoices(value);
    }

    private static List<ChoiceRow> ScopeChoices(string selected) => Choices(selected,
        ("live", "Outstanding", "Open, claimed and in progress."),
        ("all", "Everything", "Including what has already finished or failed."));

    private static string Verb(TaskState state) => state switch
    {
        TaskState.Open => "open",
        TaskState.Claimed => "claimed",
        TaskState.Assigned => "assigned",
        TaskState.Done => "done",
        _ => "failed",
    };

    /// <summary>
    /// The state, and the thing about it that an operator would want to know without opening it:
    /// an open task that nobody has taken is the one worth noticing.
    /// </summary>
    private static string Describe(TaskInfoPayload task) => task.State switch
    {
        TaskState.Open => "waiting for an agent",
        TaskState.Claimed => "claimed",
        TaskState.Assigned => "handed over by the delegator",
        TaskState.Done => "done",
        _ => "failed",
    };

    private static string Times(TaskInfoPayload task)
    {
        var parts = new List<string> { $"posted {Stamp(task.CreatedAt)}" };
        if (task.ClaimedAt is { } claimed)
        {
            parts.Add($"claimed {Stamp(claimed)}");
        }

        if (task.FinishedAt is { } finished)
        {
            parts.Add($"finished {Stamp(finished)}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// What the lease is doing. Worth its own line because it is the difference between work in
    /// hand and work about to be handed back: a task whose holder went quiet is swept back to
    /// open when this passes, and an operator watching one stall wants to see it coming.
    /// </summary>
    private static string Lease(TaskInfoPayload task)
    {
        if (task.LeaseExpiresAt is not { } expires)
        {
            return task.State is TaskState.Done or TaskState.Failed ? "finished" : "not held";
        }

        var left = DateTimeOffset.FromUnixTimeMilliseconds(expires) - DateTimeOffset.UtcNow;
        return left <= TimeSpan.Zero
            ? "expired — due to be handed back"
            : $"held for another {Rounded(left)}";
    }

    private static string Rounded(TimeSpan span) => span.TotalMinutes < 1
        ? $"{Math.Max(1, (int)span.TotalSeconds)}s"
        : span.TotalHours < 1
            ? $"{(int)span.TotalMinutes}m"
            : $"{span.TotalHours:F1}h";

    private static string Stamp(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToLocalTime().ToString("HH:mm");
}
