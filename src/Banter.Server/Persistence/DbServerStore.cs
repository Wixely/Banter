using Banter.Core;
using Dapper;

namespace Banter.Server.Persistence;

/// <summary>A page of history, oldest-first. <c>NextCursor</c> is the oldest returned message
/// id when older messages exist. A null page from the store means the cursor was unknown.</summary>
public sealed record HistoryPage(IReadOnlyList<ChatMessage> Messages, string? NextCursor);

public sealed record RoomRecord(string Name, string? Topic);

/// <summary>Room and message persistence used by the room engine.</summary>
public interface IServerStore
{
    ValueTask AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    ValueTask<HistoryPage?> GetHistoryPageAsync(string room, string? beforeMessageId, int limit, CancellationToken cancellationToken = default);
    ValueTask UpsertRoomAsync(string name, string? topic, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<RoomRecord>> GetRoomsAsync(CancellationToken cancellationToken = default);
}

public sealed class DbServerStore(BanterDatabase database) : IServerStore
{
    private sealed record MessageRow(long Seq, string MessageId, string Room, string Sender, string Text, long Timestamp, string? FileId);

    public async ValueTask AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            INSERT INTO messages (message_id, room, sender, text, timestamp, file_id)
            VALUES (@MessageId, @Room, @Sender, @Text, @Timestamp, @FileId)
            """,
            message).ConfigureAwait(false);
    }

    public async ValueTask<HistoryPage?> GetHistoryPageAsync(
        string room, string? beforeMessageId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

        long? beforeSeq = null;
        if (beforeMessageId is not null)
        {
            beforeSeq = await connection.ExecuteScalarAsync<long?>(
                "SELECT seq FROM messages WHERE room = @Room AND message_id = @MessageId",
                new { Room = room, MessageId = beforeMessageId }).ConfigureAwait(false);
            if (beforeSeq is null)
            {
                return null;
            }
        }

        var rows = (await connection.QueryAsync<MessageRow>(
            """
            SELECT seq AS Seq, message_id AS MessageId, room AS Room, sender AS Sender,
                   text AS Text, timestamp AS Timestamp, file_id AS FileId
            FROM messages
            WHERE room = @Room AND (@BeforeSeq IS NULL OR seq < @BeforeSeq)
            ORDER BY seq DESC
            LIMIT @Limit
            """,
            new { Room = room, BeforeSeq = beforeSeq, Limit = limit }).ConfigureAwait(false)).AsList();
        rows.Reverse();

        string? nextCursor = null;
        if (rows.Count > 0)
        {
            var olderExists = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM messages WHERE room = @Room AND seq < @Oldest)",
                new { Room = room, Oldest = rows[0].Seq }).ConfigureAwait(false);
            nextCursor = olderExists ? rows[0].MessageId : null;
        }

        var messages = rows
            .Select(m => new ChatMessage(m.MessageId, m.Room, m.Sender, m.Text, m.Timestamp, m.FileId))
            .ToArray();
        return new HistoryPage(messages, nextCursor);
    }

    public async ValueTask UpsertRoomAsync(string name, string? topic, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Identical upsert syntax on SQLite and PostgreSQL.
        await connection.ExecuteAsync(
            """
            INSERT INTO rooms (name, topic) VALUES (@Name, @Topic)
            ON CONFLICT (name) DO UPDATE SET topic = EXCLUDED.topic
            """,
            new { Name = name, Topic = topic }).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RoomRecord>> GetRoomsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rooms = await connection.QueryAsync<RoomRecord>(
            "SELECT name AS Name, topic AS Topic FROM rooms").ConfigureAwait(false);
        return rooms.AsList();
    }
}
