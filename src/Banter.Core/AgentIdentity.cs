using System.Security.Cryptography;
using Banter.Protocol;

namespace Banter.Core;

/// <summary>
/// What the server knows about an agent: who it is, what it may do, and the public half of the key
/// its machine holds.
///
/// <para>There is no secret in here. The private key is generated on the agent's own machine during
/// enrolment and never transmitted, so a copy of this record — or of the whole table — lets nobody
/// impersonate anybody.</para>
/// </summary>
public sealed record AgentIdentity
{
    public required string Nick { get; init; }

    public IReadOnlyList<string> Rooms { get; init; } = ["#main"];

    public IReadOnlyList<string> Skills { get; init; } = ["chat"];

    /// <summary>Whether this agent's model is ours or a third party's — decides if data may leave.</summary>
    public AgentLocality Locality { get; init; } = AgentLocality.Local;

    /// <summary>The most sensitive material this agent may be shown.</summary>
    public DataSensitivity Clearance { get; init; } = DataSensitivity.Sensitive;

    /// <summary>SubjectPublicKeyInfo of the enrolled key, or null until a machine has enrolled.</summary>
    public byte[]? PublicKey { get; init; }

    /// <summary>Whether an unredeemed enrolment code is outstanding.</summary>
    public bool EnrolmentPending { get; init; }

    /// <summary>
    /// A short, readable digest of the enrolled key, so an operator can tell one machine from
    /// another — and notice when an agent they did not re-enrol is suddenly on a different one.
    /// Empty until enrolled.
    /// </summary>
    public string KeyFingerprint => PublicKey is null ? "" : AgentKeys.Fingerprint(PublicKey);
}

/// <summary>Why an enrolment attempt was refused, so the agent is told something it can act on.</summary>
public enum AgentEnrolmentResult
{
    Enrolled,

    /// <summary>No identity holds a live code matching this one — mistyped, already used, or revoked.</summary>
    UnknownCode,

    /// <summary>The code was an identity's, but it has passed its expiry.</summary>
    Expired,

    /// <summary>The offered key is not a usable P-256 public key.</summary>
    BadKey,
}

/// <summary>
/// Where agent identities live. An interface so the server depends on the capability rather than on
/// a database — a deployment with no identity store simply refuses the admin verbs, which is what
/// the in-memory account store does.
/// </summary>
public interface IAgentIdentityStore
{
    Task<string> CreateAsync(AgentIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<string?> ReissueAsync(string nick, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        string nick, IReadOnlyList<string>? rooms, IReadOnlyList<string>? skills,
        AgentLocality? locality, DataSensitivity? clearance, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string nick, CancellationToken cancellationToken = default);

    Task<AgentIdentity?> FindAsync(string nick, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentIdentity>> ListAsync(CancellationToken cancellationToken = default);

    Task<(AgentEnrolmentResult Result, AgentIdentity? Identity)> EnrolAsync(
        string code, byte[] publicKey, DateTimeOffset now, CancellationToken cancellationToken = default);
}
