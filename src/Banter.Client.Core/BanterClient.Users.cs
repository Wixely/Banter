using Banter.Protocol;

namespace Banter.Client.Core;

/// <summary>
/// Managing who the users are — the humans' mirror of <c>BanterClient.Agents</c>. Admin-only on
/// the server apart from <see cref="ChangeMyPasswordAsync"/>, so everything else throws
/// <see cref="BanterErrorException"/> with <c>NOT_ADMIN</c> for anyone else.
///
/// <para>The one credential that ever crosses this seam is a temporary password the server just
/// invented, returned once from a create or a reset. Nothing here can read a password back,
/// because the server does not have it either.</para>
/// </summary>
public sealed partial class BanterClient
{
    /// <summary>
    /// Creates a user and returns the temporary password to hand to them, once. They should
    /// change it with <see cref="ChangeMyPasswordAsync"/> the first time they sign in; a lost
    /// one is reset with <see cref="ResetUserPasswordAsync"/>, not looked up.
    /// </summary>
    public Task<UserTempPasswordPayload> CreateUserAsync(
        string username, bool isAdmin = false, CancellationToken cancellationToken = default) =>
        RequestAsync<UserTempPasswordPayload>(new UserCreatePayload(username, isAdmin), cancellationToken);

    /// <summary>Grants or revokes admin. The server refuses the change that would leave no admin.</summary>
    public Task<OkPayload> SetUserAdminAsync(
        string username, bool isAdmin, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new UserUpdatePayload(username, isAdmin), cancellationToken);

    /// <summary>
    /// Removes a user. Their password stops working at once; a session already signed in lives
    /// until it disconnects. The server refuses to let an admin remove themselves.
    /// </summary>
    public Task<OkPayload> DeleteUserAsync(string username, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new UserDeletePayload(username), cancellationToken);

    /// <summary>A fresh temporary password for someone locked out. The old one stops working.</summary>
    public Task<UserTempPasswordPayload> ResetUserPasswordAsync(
        string username, CancellationToken cancellationToken = default) =>
        RequestAsync<UserTempPasswordPayload>(new UserPasswordResetPayload(username), cancellationToken);

    /// <summary>Every human account this server knows. Agents are on <see cref="ListAgentsAsync"/>.</summary>
    public async Task<IReadOnlyList<UserAccountPayload>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync<UsersPayload>(new UserListPayload(), cancellationToken).ConfigureAwait(false)).Users;

    /// <summary>
    /// Changes this session's own password — the only account operation that is not admin-gated.
    /// The current password is required even though the session is signed in, so a machine left
    /// unlocked is not enough to lock the real owner out.
    /// </summary>
    public Task<OkPayload> ChangeMyPasswordAsync(
        string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new PasswordChangePayload(currentPassword, newPassword), cancellationToken);
}
