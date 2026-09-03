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
    /// Creates an agent identity and returns the one-time code to paste into the machine that will
    /// run it. The code is returned once and never stored in recoverable form — a lost code is
    /// reissued with <see cref="ReissueAgentAsync"/>, not looked up.
    /// </summary>
    public Task<AgentEnrolmentCodePayload> CreateAgentAsync(
        string nick,
        IReadOnlyList<string> rooms,
        IReadOnlyList<string> skills,
        AgentLocality locality = AgentLocality.Local,
        DataSensitivity clearance = DataSensitivity.Sensitive,
        CancellationToken cancellationToken = default) =>
        RequestAsync<AgentEnrolmentCodePayload>(
            new AgentIdentityCreatePayload(
                nick, rooms, skills,
                locality.ToString().ToLowerInvariant(),
                clearance.ToString().ToLowerInvariant()),
            cancellationToken);

    /// <summary>
    /// Changes an agent. Anything left null keeps its current value, so a caller changing one field
    /// cannot silently reset the others back to a default it never chose.
    /// </summary>
    public Task<OkPayload> UpdateAgentAsync(
        string nick,
        IReadOnlyList<string>? rooms = null,
        IReadOnlyList<string>? skills = null,
        AgentLocality? locality = null,
        DataSensitivity? clearance = null,
        CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(
            new AgentIdentityUpdatePayload(
                nick, rooms, skills,
                locality?.ToString().ToLowerInvariant(),
                clearance?.ToString().ToLowerInvariant()),
            cancellationToken);

    /// <summary>Removes an agent. Its key stops working on the next thing it tries.</summary>
    public Task<OkPayload> DeleteAgentAsync(string nick, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new AgentIdentityDeletePayload(nick), cancellationToken);

    /// <summary>
    /// A fresh enrolment code for an agent whose machine is being replaced. This also retires the
    /// key the old machine holds, which is the point: a reissue is what you reach for when a laptop
    /// has been lost.
    /// </summary>
    public Task<AgentEnrolmentCodePayload> ReissueAgentAsync(string nick, CancellationToken cancellationToken = default) =>
        RequestAsync<AgentEnrolmentCodePayload>(new AgentIdentityReissuePayload(nick), cancellationToken);

    /// <summary>Every agent identity this server knows, enrolled or not.</summary>
    public async Task<IReadOnlyList<AgentIdentityPayload>> ListAgentsAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync<AgentIdentitiesPayload>(new AgentIdentityListPayload(), cancellationToken)
            .ConfigureAwait(false)).Identities;
}
