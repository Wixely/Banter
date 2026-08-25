using Banter.Protocol;

namespace Banter.Agents.Sdk;

/// <summary>
/// The work-ledger half of an agent (PLAN §8b): claiming, executing and reporting on tasks.
/// Split into its own file because it is a separate concern from conversation.
/// </summary>
public abstract partial class BanterAgent
{
    /// <summary>Tasks this agent is already executing, so a re-broadcast cannot start a second run.</summary>
    private readonly HashSet<string> _workingOn = new(StringComparer.Ordinal);

    /// <summary>Raised when this agent starts working a task, for logging and supervision.</summary>
    public event Action<TaskInfoPayload>? TaskStarted;

    /// <summary>Raised when a task this agent worked finishes, with its success flag.</summary>
    public event Action<TaskInfoPayload, bool>? TaskFinished;

    // ── Ledger operations, for a subclass or a delegator to drive directly ───────────────────

    public Task<TaskInfoPayload> PostTaskAsync(
        string room, string title, string body = "", CancellationToken cancellationToken = default) =>
        Client.PostTaskAsync(room, title, body, cancellationToken: cancellationToken);

    public Task<TaskInfoPayload> AssignTaskAsync(
        string taskId, string nick, CancellationToken cancellationToken = default) =>
        Client.AssignTaskAsync(taskId, nick, cancellationToken);

    public Task<TaskListPayload> ListTasksAsync(
        string room, bool includeFinished = false, CancellationToken cancellationToken = default) =>
        Client.ListTasksAsync(room, includeFinished, cancellationToken);

    /// <summary>Report progress, which also renews the lease on a task this agent holds.</summary>
    public Task ReportProgressAsync(string taskId, string note, CancellationToken cancellationToken = default) =>
        Client.UpdateTaskAsync(taskId, note, cancellationToken);

    // ── Automatic working ───────────────────────────────────────────────────────────────────

    private void OnTaskChanged(TaskInfoPayload task)
    {
        if (Options.TaskWork is not { } work)
        {
            return;
        }

        var mine = string.Equals(task.Assignee, Nick, StringComparison.OrdinalIgnoreCase);

        // Assigned to us: do it regardless of whether we take work off the board ourselves,
        // because that is the delegator handing it over.
        if (task.State == TaskState.Assigned && mine)
        {
            Start(task);
            return;
        }

        // Claimed by us via our own claim below; the broadcast is the confirmation.
        if (task.State == TaskState.Claimed && mine)
        {
            Start(task);
            return;
        }

        if (task.State == TaskState.Open && work.ClaimOpenTasks && Matches(task))
        {
            _ = TryClaimAsync(task);
        }
    }

    /// <summary>Whether this agent's skills look relevant to the task's text.</summary>
    private bool Matches(TaskInfoPayload task)
    {
        if (Options.Skills.Count == 0)
        {
            return true;
        }

        var text = $"{task.Title} {task.Body}".ToLowerInvariant();
        return Options.Skills.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    private async Task TryClaimAsync(TaskInfoPayload task)
    {
        try
        {
            await Client.ClaimTaskAsync(task.TaskId, _stopping.Token).ConfigureAwait(false);
            // The claim's broadcast comes back through OnTaskChanged and starts the work, so
            // there is one path into execution rather than two that could both fire.
        }
        catch (Client.Core.BanterErrorException)
        {
            // Lost the race, hit the concurrency cap, or it was withdrawn. All ordinary.
        }
        catch (Exception)
        {
            // Transport trouble; the task stays on the board for someone else.
        }
    }

    private void Start(TaskInfoPayload task)
    {
        lock (_workingOn)
        {
            if (!_workingOn.Add(task.TaskId))
            {
                return;
            }
        }

        _ = Task.Run(() => WorkAsync(task));
    }

    private async Task WorkAsync(TaskInfoPayload task)
    {
        var work = Options.TaskWork!;
        using var working = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);

        // Renew the lease while the job runs. Without this a task that takes longer than the
        // lease is reclaimed mid-flight and handed to another agent, so two would be doing it.
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(work.ProgressInterval);
                while (await timer.WaitForNextTickAsync(working.Token).ConfigureAwait(false))
                {
                    await ReportProgressAsync(task.TaskId, "still working", working.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Cancelled at the end of the job, or the renew failed; the outcome below is
                // what matters and a failed renew surfaces as a lost task there.
            }
        });

        var success = false;
        var result = "";
        try
        {
            TaskStarted?.Invoke(task);
            var prompt = task.Body.Length > 0 ? $"{task.Title}\n\n{task.Body}" : task.Title;

            var answer = new System.Text.StringBuilder();
            await foreach (var piece in RespondAsync(task.Room, task.Poster, prompt, working.Token)
                .WithCancellation(working.Token).ConfigureAwait(false))
            {
                answer.Append(piece);
            }

            result = answer.ToString().Trim();
            success = result.Length > 0;
            if (!success)
            {
                result = "produced no output";
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-task: release it rather than failing it, so it goes back on the
            // board for someone else instead of being recorded as a failure nobody caused.
            await working.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            await SafeReleaseAsync(task).ConfigureAwait(false);
            Forget(task.TaskId);
            return;
        }
        catch (Exception ex)
        {
            result = ex.Message;
            success = false;
        }

        await working.CancelAsync().ConfigureAwait(false);
        await heartbeat.ConfigureAwait(false);

        try
        {
            await Client.CompleteTaskAsync(task.TaskId, Truncate(result), success, _stopping.Token)
                .ConfigureAwait(false);
            TaskFinished?.Invoke(task, success);
        }
        catch (Exception)
        {
            // The lease will expire and the work returns to the board, which is the right
            // outcome when we cannot report what happened.
        }
        finally
        {
            Forget(task.TaskId);
        }
    }

    private async Task SafeReleaseAsync(TaskInfoPayload task)
    {
        try
        {
            await Client.ReleaseTaskAsync(task.TaskId, "agent stopping", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort; the lease is the backstop.
        }
    }

    private void Forget(string taskId)
    {
        lock (_workingOn)
        {
            _workingOn.Remove(taskId);
        }
    }

    /// <summary>Task results are stored and announced, so keep them room-sized.</summary>
    private static string Truncate(string text) =>
        text.Length <= 2000 ? text : text[..2000] + "…";
}
