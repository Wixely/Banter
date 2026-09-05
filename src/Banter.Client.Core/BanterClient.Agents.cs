using Banter.Protocol;

namespace Banter.Client.Core;

/// <summary>
/// Managing who the agents are. Admin-only on the server, so every one of these throws
/// <see cref="BanterErrorException"/> with <c>NOT_ADMIN</c> for anyone else.
///
/// <para>This is the seam an admin page sits on. Nothing here handles a private key, because there
/// is never one to handle: an agent's key is made on its own machine during enrolment and the
/// server only ever sees the public half.</para>
/// </summary>
public sealed partial class BanterClient
{
    /// <summary>
    /// Creates an agent identity and returns the one-time code to redeem wherever the agent will
    /// run. The code is returned once and never stored in recoverable form — a lost code is
    /// reissued with <see cref="ReissueAgentAsync"/>, not looked up.
    /// </summary>
    public Task<AgentEnrolmentCodePayload> CreateAgentAsync(
        string nick,
        IReadOnlyList<string> rooms,
        IReadOnlyList<string> skills,
        AgentLocality locality = AgentLocality.Local,
        DataSensitivity clearance = DataSensitivity.Sensitive,
        int? costTier = null,
        bool? wantsDelegator = null,
        CancellationToken cancellationToken = default) =>
        RequestAsync<AgentEnrolmentCodePayload>(
            new AgentIdentityCreatePayload(
                nick, rooms, skills,
                locality.ToString().ToLowerInvariant(),
                clearance.ToString().ToLowerInvariant(),
                costTier, wantsDelegator),
            cancellationToken);

    /// <summary>
    /// Changes an agent. Anything left null keeps its current value, so a caller changing one field
    /// cannot silently reset the others back to a default it never chose.
    /// </summary>
    /// <param name="costTier">An override to set, or null to leave the current state alone —
    /// unless <paramref name="clearCostTier"/> is true, which hands the choice back to the agent.
    /// <paramref name="wantsDelegator"/>/<paramref name="clearWantsDelegator"/> mirror this.</param>
    public Task<OkPayload> UpdateAgentAsync(
        string nick,
        IReadOnlyList<string>? rooms = null,
        IReadOnlyList<string>? skills = null,
        AgentLocality? locality = null,
        DataSensitivity? clearance = null,
        int? costTier = null,
        bool clearCostTier = false,
        bool? wantsDelegator = null,
        bool clearWantsDelegator = false,
        CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(
            new AgentIdentityUpdatePayload(
                nick, rooms, skills,
                locality?.ToString().ToLowerInvariant(),
                clearance?.ToString().ToLowerInvariant(),
                SetCostTier: costTier is not null || clearCostTier,
                CostTier: clearCostTier ? null : costTier,
                SetWantsDelegator: wantsDelegator is not null || clearWantsDelegator,
                WantsDelegator: clearWantsDelegator ? null : wantsDelegator),
            cancellationToken);

    /// <summary>Removes an agent. Its key stops working on the next thing it tries.</summary>
    public Task<OkPayload> DeleteAgentAsync(string nick, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new AgentIdentityDeletePayload(nick), cancellationToken);

    /// <summary>
    /// A fresh enrolment code, which also retires the key currently enrolled — that is the point.
    /// A reissue is what you reach for when the key has been lost or has to be moved: an identity
    /// holds exactly one key, so whatever was running on the old one stops.
    /// </summary>
    public Task<AgentEnrolmentCodePayload> ReissueAgentAsync(string nick, CancellationToken cancellationToken = default) =>
        RequestAsync<AgentEnrolmentCodePayload>(new AgentIdentityReissuePayload(nick), cancellationToken);

    /// <summary>Every agent identity this server knows, enrolled or not.</summary>
    public async Task<IReadOnlyList<AgentIdentityPayload>> ListAgentsAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync<AgentIdentitiesPayload>(new AgentIdentityListPayload(), cancellationToken)
            .ConfigureAwait(false)).Identities;
}
