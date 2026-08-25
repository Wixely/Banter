using Banter.Protocol;
using Dapper;

namespace Banter.Server.Persistence;

/// <summary>
/// Persistence for the work ledger (PLAN §8b). Tasks outlive both the agent holding them and the
/// server process, which is the point — a crashed agent's work has to be recoverable, not lost.
///
/// <para>All arbitration (who wins a race to claim) happens on the room engine's single-writer
/// loop, so this type does no locking of its own; it is storage, not policy.</para>
/// </summary>
public sealed class TaskStore(BanterDatabase database)
{
    /// <summary>Mutable class, not a record: Dapper property-maps provider numerics (SQLite
    /// INTEGER is Int64) where constructor mapping would demand exact types.</summary>
    private sealed class TaskRow
    {
        public string TaskId { get; set; } = "";
        public string Room { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Poster { get; set; } = "";
        public long State { get; set; }
        public string? Assignee { get; set; }
        public long CreatedAt { get; set; }
        public long? ClaimedAt { get; set; }
        public long? FinishedAt { get; set; }
        public long? LeaseExpiresAt { get; set; }
        public long LeaseSeconds { get; set; }
        public string? Result { get; set; }

        public TaskInfoPayload ToPayload() => new(
            TaskId, Room, Title, Body, Poster, (TaskState)State, Assignee,
            CreatedAt, ClaimedAt, FinishedAt, LeaseExpiresAt, Result);
    }

    private const string Select = """
        SELECT task_id AS TaskId, room AS Room, title AS Title, body AS Body, poster AS Poster,
               state AS State, assignee AS Assignee, created_at AS CreatedAt, claimed_at AS ClaimedAt,
               finished_at AS FinishedAt, lease_expires_at AS LeaseExpiresAt,
               lease_seconds AS LeaseSeconds, result AS Result
        FROM tasks
        """;

    public async Task<TaskInfoPayload> CreateAsync(
        string room, string title, string body, string poster, int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        var task = new TaskInfoPayload(
            Guid.NewGuid().ToString("N"), room, title, body, poster,
            TaskState.Open, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            null, null, null, null);

        await using var connection = database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO tasks (task_id, room, title, body, poster, state, created_at, lease_seconds)
            VALUES (@TaskId, @Room, @Title, @Body, @Poster, @State, @CreatedAt, @LeaseSeconds)
            """,
            new
            {
                task.TaskId, task.Room, task.Title, task.Body, task.Poster,
                State = (int)TaskState.Open, task.CreatedAt, LeaseSeconds = leaseSeconds,
            }).ConfigureAwait(false);

        return task;
    }

    public async Task<TaskInfoPayload?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<TaskRow>(
            Select + "\nWHERE task_id = @taskId", new { taskId }).ConfigureAwait(false);
        return row?.ToPayload();
    }

    /// <summary>Tasks in a room, newest first. Terminal ones are excluded unless asked for.</summary>
    public async Task<IReadOnlyList<TaskInfoPayload>> ListAsync(
        string room, bool includeFinished, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        var sql = Select + "\nWHERE room = @room" +
                  (includeFinished ? "" : "\n  AND state IN (0, 1, 2)") +
                  "\nORDER BY created_at DESC";
        var rows = await connection.QueryAsync<TaskRow>(sql, new { room }).ConfigureAwait(false);
        return rows.Select(r => r.ToPayload()).ToList();
    }

    /// <summary>How many live tasks an agent holds, for the concurrency cap.</summary>
    public async Task<int> HeldCountAsync(string nick, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tasks WHERE assignee = @nick AND state IN (1, 2)",
            new { nick }).ConfigureAwait(false);
    }

    /// <summary>
    /// Take an open task for <paramref name="nick"/>. The <c>state = 0</c> predicate is the
    /// arbitration: a second claim updates zero rows and is refused, so two agents cannot both
    /// believe they hold the same work even if the writes interleave.
    /// </summary>
    public async Task<TaskInfoPayload?> TryTakeAsync(
        string taskId, string nick, TaskState newState, int defaultLeaseSeconds,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var connection = database.CreateConnection();
        var updated = await connection.ExecuteAsync(
            """
            UPDATE tasks
               SET state = @State,
                   assignee = @Nick,
                   claimed_at = @Now,
                   lease_expires_at = @Now + (CASE WHEN lease_seconds > 0
                                                   THEN lease_seconds ELSE @DefaultLease END) * 1000
             WHERE task_id = @TaskId AND state = 0
            """,
            new { State = (int)newState, Nick = nick, Now = now, TaskId = taskId, DefaultLease = defaultLeaseSeconds })
            .ConfigureAwait(false);

        return updated == 0 ? null : await GetAsync(taskId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Extend the lease on a held task. Returns false when the caller does not hold it.</summary>
    public async Task<bool> TryRenewAsync(
        string taskId, string nick, int defaultLeaseSeconds, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var connection = database.CreateConnection();
        var updated = await connection.ExecuteAsync(
            """
            UPDATE tasks
               SET lease_expires_at = @Now + (CASE WHEN lease_seconds > 0
                                                   THEN lease_seconds ELSE @DefaultLease END) * 1000
             WHERE task_id = @TaskId AND assignee = @Nick AND state IN (1, 2)
            """,
            new { Now = now, TaskId = taskId, Nick = nick, DefaultLease = defaultLeaseSeconds })
            .ConfigureAwait(false);

        return updated > 0;
    }

    /// <summary>Return a task to the pool. <paramref name="nick"/> null releases regardless of
    /// holder, which is how the server reclaims an expired lease.</summary>
    public async Task<bool> TryReleaseAsync(
        string taskId, string? nick, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        var updated = await connection.ExecuteAsync(
            """
            UPDATE tasks
               SET state = 0, assignee = NULL, claimed_at = NULL, lease_expires_at = NULL
             WHERE task_id = @TaskId AND state IN (1, 2)
               AND (@Nick IS NULL OR assignee = @Nick)
            """,
            new { TaskId = taskId, Nick = nick }).ConfigureAwait(false);

        return updated > 0;
    }

    /// <summary>Finish a task. Only its holder may.</summary>
    public async Task<bool> TryFinishAsync(
        string taskId, string nick, bool success, string result, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        var updated = await connection.ExecuteAsync(
            """
            UPDATE tasks
               SET state = @State, finished_at = @Now, lease_expires_at = NULL, result = @Result
             WHERE task_id = @TaskId AND assignee = @Nick AND state IN (1, 2)
            """,
            new
            {
                State = (int)(success ? TaskState.Done : TaskState.Failed),
                Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Result = result,
                TaskId = taskId,
                Nick = nick,
            }).ConfigureAwait(false);

        return updated > 0;
    }

    /// <summary>Tasks whose lease has lapsed, for the reclaim sweep.</summary>
    public async Task<IReadOnlyList<TaskInfoPayload>> ExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<TaskRow>(
            Select + "\nWHERE state IN (1, 2) AND lease_expires_at IS NOT NULL AND lease_expires_at <= @now",
            new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }).ConfigureAwait(false);
        return rows.Select(r => r.ToPayload()).ToList();
    }
}
