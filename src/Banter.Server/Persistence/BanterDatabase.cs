using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Banter.Server.Persistence;

/// <summary>Storage backends. SQLite is the zero-setup default; PostgreSQL is the hosted
/// option — same stores and manifest, per-provider SQL where dialects differ.</summary>
public enum StorageProvider
{
    Sqlite,
    Postgres,
}

public sealed record BanterStorageOptions(StorageProvider Provider, string ConnectionString)
{
    public static BanterStorageOptions DefaultSqlite(string path = "banter.db") =>
        new(StorageProvider.Sqlite, $"Data Source={path}");

    public static BanterStorageOptions Parse(string? provider, string? connectionString) =>
        (provider ?? "sqlite").ToLowerInvariant() switch
        {
            "sqlite" => new(StorageProvider.Sqlite, connectionString ?? "Data Source=banter.db"),
            "postgres" or "postgresql" or "npgsql" => new(
                StorageProvider.Postgres,
                connectionString ?? throw new ArgumentException("PostgreSQL requires --connection <connection-string>.")),
            var other => throw new ArgumentException($"Unknown storage provider '{other}' (expected sqlite or postgres)."),
        };
}

/// <summary>
/// Connection factory plus hand-rolled schema management: an ordered migration list applied
/// against a <c>schema_manifest</c> table, each migration in its own transaction. No ORM —
/// stores use Dapper over one short-lived connection per operation.
/// </summary>
public sealed class BanterDatabase(BanterStorageOptions options)
{
    public BanterStorageOptions Options { get; } = options;

    public DbConnection CreateConnection() => Options.Provider switch
    {
        StorageProvider.Sqlite => new SqliteConnection(Options.ConnectionString),
        StorageProvider.Postgres => new NpgsqlConnection(Options.ConnectionString),
        _ => throw new InvalidOperationException($"Unhandled provider {Options.Provider}."),
    };

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Applies pending migrations in order, recording each in the manifest. Safe to
    /// call on every startup; already-applied migrations are skipped by id.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS schema_manifest (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            )
            """).ConfigureAwait(false);

        var applied = (await connection.QueryAsync<int>("SELECT id FROM schema_manifest").ConfigureAwait(false)).ToHashSet();
        foreach (var migration in SchemaManifest.Migrations)
        {
            if (applied.Contains(migration.Id))
            {
                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var sql = Options.Provider == StorageProvider.Sqlite ? migration.SqliteSql : migration.PostgresSql;
            await connection.ExecuteAsync(sql, transaction: transaction).ConfigureAwait(false);
            await connection.ExecuteAsync(
                "INSERT INTO schema_manifest (id, name, applied_at_utc) VALUES (@Id, @Name, @AppliedAtUtc)",
                new { migration.Id, migration.Name, AppliedAtUtc = DateTime.UtcNow.ToString("O") },
                transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>SQLite pools connections per data source; tests that delete their database
    /// file need the pool cleared first. No-op concern for other providers.</summary>
    public static void ClearSqlitePools() => SqliteConnection.ClearAllPools();
}

/// <summary>One migration, expressed per dialect. Never edit a shipped migration — append a
/// new one; ids are the manifest's ordering and identity.</summary>
public sealed record Migration(int Id, string Name, string SqliteSql, string PostgresSql);

public static class SchemaManifest
{
    public static IReadOnlyList<Migration> Migrations { get; } =
    [
        new(
            1,
            "initial-accounts-rooms-messages",
            SqliteSql:
            """
            CREATE TABLE accounts (
                username TEXT NOT NULL PRIMARY KEY,
                password_hash BLOB NOT NULL,
                password_salt BLOB NOT NULL,
                iterations INTEGER NOT NULL,
                is_agent INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE rooms (
                name TEXT NOT NULL PRIMARY KEY,
                topic TEXT NULL
            );

            CREATE TABLE messages (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id TEXT NOT NULL UNIQUE,
                room TEXT NOT NULL,
                sender TEXT NOT NULL,
                text TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                file_id TEXT NULL
            );

            CREATE INDEX ix_messages_room_seq ON messages (room, seq);
            """,
            PostgresSql:
            """
            CREATE TABLE accounts (
                username TEXT NOT NULL PRIMARY KEY,
                password_hash BYTEA NOT NULL,
                password_salt BYTEA NOT NULL,
                iterations INTEGER NOT NULL,
                is_agent BOOLEAN NOT NULL DEFAULT FALSE
            );

            CREATE TABLE rooms (
                name TEXT NOT NULL PRIMARY KEY,
                topic TEXT NULL
            );

            CREATE TABLE messages (
                seq BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                message_id TEXT NOT NULL UNIQUE,
                room TEXT NOT NULL,
                sender TEXT NOT NULL,
                text TEXT NOT NULL,
                timestamp BIGINT NOT NULL,
                file_id TEXT NULL
            );

            CREATE INDEX ix_messages_room_seq ON messages (room, seq);
            """),
        new(
            2,
            "room-scoped-file-storage",
            SqliteSql:
            """
            CREATE TABLE files (
                file_id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                mime TEXT NOT NULL,
                size INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                uploader TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                description TEXT NULL,
                complete INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX ix_files_sha256 ON files (sha256);

            CREATE TABLE file_grants (
                file_id TEXT NOT NULL,
                room TEXT NOT NULL,
                PRIMARY KEY (file_id, room)
            );

            CREATE INDEX ix_file_grants_room ON file_grants (room);
            """,
            PostgresSql:
            """
            CREATE TABLE files (
                file_id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                mime TEXT NOT NULL,
                size BIGINT NOT NULL,
                sha256 TEXT NOT NULL,
                uploader TEXT NOT NULL,
                created_at BIGINT NOT NULL,
                description TEXT NULL,
                complete BOOLEAN NOT NULL DEFAULT FALSE
            );

            CREATE INDEX ix_files_sha256 ON files (sha256);

            CREATE TABLE file_grants (
                file_id TEXT NOT NULL,
                room TEXT NOT NULL,
                PRIMARY KEY (file_id, room)
            );

            CREATE INDEX ix_file_grants_room ON file_grants (room);
            """),
        new(
            3,
            "work-ledger",
            SqliteSql:
            """
            CREATE TABLE tasks (
                task_id TEXT NOT NULL PRIMARY KEY,
                room TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL DEFAULT '',
                poster TEXT NOT NULL,
                state INTEGER NOT NULL DEFAULT 0,
                assignee TEXT NULL,
                created_at INTEGER NOT NULL,
                claimed_at INTEGER NULL,
                finished_at INTEGER NULL,
                lease_expires_at INTEGER NULL,
                lease_seconds INTEGER NOT NULL,
                result TEXT NULL
            );

            CREATE INDEX ix_tasks_room_state ON tasks (room, state);
            CREATE INDEX ix_tasks_lease ON tasks (lease_expires_at);
            """,
            PostgresSql:
            """
            CREATE TABLE tasks (
                task_id TEXT NOT NULL PRIMARY KEY,
                room TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL DEFAULT '',
                poster TEXT NOT NULL,
                state INTEGER NOT NULL DEFAULT 0,
                assignee TEXT NULL,
                created_at BIGINT NOT NULL,
                claimed_at BIGINT NULL,
                finished_at BIGINT NULL,
                lease_expires_at BIGINT NULL,
                lease_seconds INTEGER NOT NULL,
                result TEXT NULL
            );

            CREATE INDEX ix_tasks_room_state ON tasks (room, state);
            CREATE INDEX ix_tasks_lease ON tasks (lease_expires_at);
            """),
        new(
            4,
            "admin-accounts",
            SqliteSql: "ALTER TABLE accounts ADD COLUMN is_admin INTEGER NOT NULL DEFAULT 0;",
            PostgresSql: "ALTER TABLE accounts ADD COLUMN is_admin BOOLEAN NOT NULL DEFAULT FALSE;"),
        new(
            5,
            "agent-tool-grants",
            SqliteSql:
            """
            CREATE TABLE tool_grants (
                agent TEXT NOT NULL,
                tool TEXT NOT NULL,
                PRIMARY KEY (agent, tool)
            );
            """,
            PostgresSql:
            """
            CREATE TABLE tool_grants (
                agent TEXT NOT NULL,
                tool TEXT NOT NULL,
                PRIMARY KEY (agent, tool)
            );
            """),
        new(
            6,
            "message-edit-and-delete",
            // Two timestamps rather than a flag and a copy of the old text: an audit of what was
            // said before is a different feature with different consent, and keeping it here would
            // mean "delete" quietly retained the words it promised to remove.
            SqliteSql:
            """
            ALTER TABLE messages ADD COLUMN edited_at INTEGER NULL;
            ALTER TABLE messages ADD COLUMN deleted_at INTEGER NULL;
            """,
            PostgresSql:
            """
            ALTER TABLE messages ADD COLUMN edited_at BIGINT NULL;
            ALTER TABLE messages ADD COLUMN deleted_at BIGINT NULL;
            """),
        new(
            7,
            "agent-identities",
            // The public key is nullable because an identity exists before any machine has claimed
            // it: an admin creates it, and it sits unenrolled until somebody redeems its code.
            //
            // The enrolment code is stored HASHED, like a password, for the same reason. It is a
            // bearer secret while it lives, and a table an operator can read over someone's
            // shoulder should not hand out the right to become an agent.
            SqliteSql:
            """
            CREATE TABLE agent_identities (
                nick TEXT PRIMARY KEY,
                rooms TEXT NOT NULL,
                skills TEXT NOT NULL,
                locality TEXT NOT NULL,
                clearance TEXT NOT NULL,
                public_key BLOB NULL,
                enrolment_hash BLOB NULL,
                enrolment_salt BLOB NULL,
                enrolment_expires_at INTEGER NULL,
                created_at INTEGER NOT NULL
            );
            """,
            PostgresSql:
            """
            CREATE TABLE agent_identities (
                nick TEXT PRIMARY KEY,
                rooms TEXT NOT NULL,
                skills TEXT NOT NULL,
                locality TEXT NOT NULL,
                clearance TEXT NOT NULL,
                public_key BYTEA NULL,
                enrolment_hash BYTEA NULL,
                enrolment_salt BYTEA NULL,
                enrolment_expires_at BIGINT NULL,
                created_at BIGINT NOT NULL
            );
            """),

        new(8,
            "agent-identity-overrides",
            // Nullable on purpose: null is a meaning, not an absence — "the agent's announced
            // value stands". Locality and clearance never needed this because the identity always
            // answers for them; cost and the delegator wish are the agent's until an admin says
            // otherwise.
            SqliteSql:
            """
            ALTER TABLE agent_identities ADD COLUMN cost_tier INTEGER NULL;
            ALTER TABLE agent_identities ADD COLUMN wants_delegator INTEGER NULL;
            """,
            PostgresSql:
            """
            ALTER TABLE agent_identities ADD COLUMN cost_tier INTEGER NULL;
            ALTER TABLE agent_identities ADD COLUMN wants_delegator BOOLEAN NULL;
            """),
    ];
}
