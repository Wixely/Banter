using System.Buffers.Text;
using System.Security.Cryptography;
using Banter.Core;
using Banter.Protocol;
using Dapper;

namespace Banter.Server.Persistence;

/// <summary>
/// Agent identities and the one-time codes that let a machine claim one.
///
/// <para>The server is the authority here, which is the whole reason this lives in a table rather
/// than in signed credentials the way a peer-to-peer channel would need. Removing an agent is a row
/// delete and takes effect on the next thing it tries to do — there is no credential still in the
/// wild that has to be waited out.</para>
/// </summary>
public sealed class AgentIdentityStore(BanterDatabase database) : IAgentIdentityStore
{
    /// <summary>
    /// How long a code is good for. Long enough to walk to another machine, short enough that a
    /// code left in a chat log or a clipboard is worthless by the time anyone finds it. It is spent
    /// on first use regardless, so this only bounds a code that is never redeemed at all.
    /// </summary>
    public static readonly TimeSpan EnrolmentWindow = TimeSpan.FromHours(1);

    // Properties rather than a positional record: Dapper's property mapping converts
    // provider-specific numerics (SQLite INTEGER → bool?, PostgreSQL BOOLEAN), where constructor
    // mapping demands exact types. Still a record, because EnrolAsync uses `with`.
    private sealed record Row
    {
        public string Nick { get; set; } = "";
        public string Rooms { get; set; } = "";
        public string Skills { get; set; } = "";
        public string Locality { get; set; } = "";
        public string Clearance { get; set; } = "";
        public int? CostTier { get; set; }
        public bool? WantsDelegator { get; set; }
        public byte[]? PublicKey { get; set; }
        public byte[]? EnrolmentHash { get; set; }
        public byte[]? EnrolmentSalt { get; set; }
        public long? EnrolmentExpiresAt { get; set; }
    }

    /// <summary>
    /// Creates the identity and returns the code to hand to whoever will run it. The code is
    /// returned here and nowhere else: only its hash is stored, so it cannot be recovered later —
    /// a lost code is reissued, never looked up.
    /// </summary>
    public async Task<string> CreateAsync(AgentIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var code = NewCode();
        var (hash, salt) = PasswordHasher.Hash(code);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_identities
                (nick, rooms, skills, locality, clearance, cost_tier, wants_delegator, public_key,
                 enrolment_hash, enrolment_salt, enrolment_expires_at, created_at)
            VALUES (@Nick, @Rooms, @Skills, @Locality, @Clearance, @CostTier, @WantsDelegator, NULL,
                    @Hash, @Salt, @Expires, @Created)
            """,
            new
            {
                Nick = Normalize(identity.Nick),
                Rooms = Join(identity.Rooms),
                Skills = Join(identity.Skills),
                Locality = identity.Locality.ToString().ToLowerInvariant(),
                Clearance = identity.Clearance.ToString().ToLowerInvariant(),
                identity.CostTier,
                identity.WantsDelegator,
                Hash = hash,
                Salt = salt,
                Expires = (now + EnrolmentWindow).ToUnixTimeSeconds(),
                Created = now.ToUnixTimeSeconds(),
            }).ConfigureAwait(false);

        return code;
    }

    /// <summary>
    /// A fresh code for an identity whose machine is being replaced, and — deliberately — the
    /// removal of the key it had. A reissue is what an operator reaches for when a laptop is lost,
    /// so it must not leave the old machine able to carry on.
    /// </summary>
    public async Task<string?> ReissueAsync(string nick, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var code = NewCode();
        var (hash, salt) = PasswordHasher.Hash(code);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(
            """
            UPDATE agent_identities
               SET public_key = NULL, enrolment_hash = @Hash, enrolment_salt = @Salt, enrolment_expires_at = @Expires
             WHERE nick = @Nick
            """,
            new
            {
                Nick = Normalize(nick),
                Hash = hash,
                Salt = salt,
                Expires = (now + EnrolmentWindow).ToUnixTimeSeconds(),
            }).ConfigureAwait(false);

        return changed == 0 ? null : code;
    }

    /// <summary>
    /// Changes what an identity may do. Null arguments leave that field alone — except the
    /// override tuples, where null is a value ("the agent decides") and the Set flag is what says
    /// whether to write it. The CASE guards use integer flags rather than boolean parameters so
    /// the same SQL runs on both dialects.
    /// </summary>
    public async Task<bool> UpdateAsync(
        string nick,
        IReadOnlyList<string>? rooms,
        IReadOnlyList<string>? skills,
        AgentLocality? locality,
        DataSensitivity? clearance,
        (bool Set, int? Value) costTier = default,
        (bool Set, bool? Value) wantsDelegator = default,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(
            """
            UPDATE agent_identities
               SET rooms     = COALESCE(@Rooms, rooms),
                   skills    = COALESCE(@Skills, skills),
                   locality  = COALESCE(@Locality, locality),
                   clearance = COALESCE(@Clearance, clearance),
                   cost_tier       = CASE WHEN @SetCost = 1 THEN @CostTier ELSE cost_tier END,
                   wants_delegator = CASE WHEN @SetWants = 1 THEN @WantsDelegator ELSE wants_delegator END
             WHERE nick = @Nick
            """,
            new
            {
                Nick = Normalize(nick),
                Rooms = rooms is null ? null : Join(rooms),
                Skills = skills is null ? null : Join(skills),
                Locality = locality?.ToString().ToLowerInvariant(),
                Clearance = clearance?.ToString().ToLowerInvariant(),
                SetCost = costTier.Set ? 1 : 0,
                CostTier = costTier.Value,
                SetWants = wantsDelegator.Set ? 1 : 0,
                WantsDelegator = wantsDelegator.Value,
            }).ConfigureAwait(false);

        return changed > 0;
    }

    public async Task<bool> DeleteAsync(string nick, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            "DELETE FROM agent_identities WHERE nick = @Nick",
            new { Nick = Normalize(nick) }).ConfigureAwait(false) > 0;
    }

    public async Task<AgentIdentity?> FindAsync(string nick, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            """
            SELECT nick AS Nick, rooms AS Rooms, skills AS Skills, locality AS Locality,
                   clearance AS Clearance, cost_tier AS CostTier, wants_delegator AS WantsDelegator,
                   public_key AS PublicKey, enrolment_hash AS EnrolmentHash,
                   enrolment_salt AS EnrolmentSalt, enrolment_expires_at AS EnrolmentExpiresAt
              FROM agent_identities WHERE nick = @Nick
            """,
            new { Nick = Normalize(nick) }).ConfigureAwait(false);

        return row is null ? null : ToIdentity(row);
    }

    public async Task<IReadOnlyList<AgentIdentity>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(
            """
            SELECT nick AS Nick, rooms AS Rooms, skills AS Skills, locality AS Locality,
                   clearance AS Clearance, cost_tier AS CostTier, wants_delegator AS WantsDelegator,
                   public_key AS PublicKey, enrolment_hash AS EnrolmentHash,
                   enrolment_salt AS EnrolmentSalt, enrolment_expires_at AS EnrolmentExpiresAt
              FROM agent_identities ORDER BY nick
            """).ConfigureAwait(false);

        return [.. rows.Select(ToIdentity)];
    }

    /// <summary>
    /// Redeems a code, registering the key the agent's machine just made.
    ///
    /// <para>The code names no identity, so every live code has to be tried — which is also what
    /// makes it safe to hand out: a code is the whole of the claim, and knowing an agent's nick
    /// gets you no closer to one. Codes are 160 bits of randomness and there are only ever a
    /// handful outstanding.</para>
    /// </summary>
    public async Task<(AgentEnrolmentResult Result, AgentIdentity? Identity)> EnrolAsync(
        string code, byte[] publicKey, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!AgentKeys.IsUsablePublicKey(publicKey))
        {
            return (AgentEnrolmentResult.BadKey, null);
        }

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await connection.QueryAsync<Row>(
            """
            SELECT nick AS Nick, rooms AS Rooms, skills AS Skills, locality AS Locality,
                   clearance AS Clearance, cost_tier AS CostTier, wants_delegator AS WantsDelegator,
                   public_key AS PublicKey, enrolment_hash AS EnrolmentHash,
                   enrolment_salt AS EnrolmentSalt, enrolment_expires_at AS EnrolmentExpiresAt
              FROM agent_identities WHERE enrolment_hash IS NOT NULL
            """).ConfigureAwait(false);

        foreach (var row in candidates)
        {
            if (row.EnrolmentHash is null || row.EnrolmentSalt is null
                || !PasswordHasher.Verify(code, row.EnrolmentHash, row.EnrolmentSalt, PasswordHasher.DefaultIterations))
            {
                continue;
            }

            if (row.EnrolmentExpiresAt is not { } expires || expires < now.ToUnixTimeSeconds())
            {
                return (AgentEnrolmentResult.Expired, null);
            }

            // Spent in the same statement that records the key: the code and the enrolment are one
            // act, and a code that survived a failure here would be a second chance nobody granted.
            await connection.ExecuteAsync(
                """
                UPDATE agent_identities
                   SET public_key = @PublicKey, enrolment_hash = NULL,
                       enrolment_salt = NULL, enrolment_expires_at = NULL
                 WHERE nick = @Nick
                """,
                new { row.Nick, PublicKey = publicKey }).ConfigureAwait(false);

            return (AgentEnrolmentResult.Enrolled, ToIdentity(row with { PublicKey = publicKey, EnrolmentHash = null }));
        }

        return (AgentEnrolmentResult.UnknownCode, null);
    }

    /// <summary>
    /// 160 bits, Base64Url. Long enough that guessing is hopeless, short enough to paste in one
    /// piece, and no ambiguity about what it is when it turns up in a message.
    /// </summary>
    private static string NewCode() =>
        "banter-enrol-" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(20));

    private static AgentIdentity ToIdentity(Row row) => new()
    {
        Nick = row.Nick,
        Rooms = Split(row.Rooms),
        Skills = Split(row.Skills),
        Locality = Enum.TryParse<AgentLocality>(row.Locality, ignoreCase: true, out var locality)
            ? locality
            : AgentLocality.Local,
        Clearance = Enum.TryParse<DataSensitivity>(row.Clearance, ignoreCase: true, out var clearance)
            ? clearance
            : DataSensitivity.Sensitive,
        CostTier = row.CostTier,
        WantsDelegator = row.WantsDelegator,
        PublicKey = row.PublicKey,
        EnrolmentPending = row.EnrolmentHash is not null,
    };

    private static string Normalize(string nick) => nick.Trim().ToLowerInvariant();

    private static string Join(IReadOnlyList<string> values) =>
        string.Join(',', values.Select(v => v.Trim()).Where(v => v.Length > 0));

    private static IReadOnlyList<string> Split(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
