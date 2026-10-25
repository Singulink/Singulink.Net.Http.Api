namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Provides <see cref="PasskeyService"/> with access to the app's stored passkeys. Apps implement it over their own storage (typically a database table)
/// and register it with <see cref="PasskeyServiceCollectionExtensions.AddPasskeys{TStore}"/>.
/// </summary>
/// <remarks>
/// The store is resolved per request (scoped), so it can use a scoped database context. Passkeys are added and removed by the app itself: a new passkey's
/// data is returned by <see cref="PasskeyService.FinishRegistrationAsync(string, string, string, string?, CancellationToken)"/> for the app to store along
/// with its own values.
/// </remarks>
public interface IPasskeyStore
{
    /// <summary>
    /// Finds a passkey by its credential ID, or returns <see langword="null"/> if no passkey has that credential ID.
    /// </summary>
    /// <param name="credentialId">The credential ID.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<PasskeyRecord?> FindAsync(byte[] credentialId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets all the passkeys of a user. They are used to limit sign-ins and verifications to the user's passkeys, and to stop a user from adding a
    /// second passkey to an authenticator that already has one for them.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<PasskeyRecord>> GetUserPasskeysAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the state a passkey reported when it was used to sign in or verify a user: its <see cref="PasskeyRecord.SignCount"/> and
    /// <see cref="PasskeyRecord.IsBackedUp"/> values, and the time it was used.
    /// </summary>
    /// <param name="passkey">The passkey with its updated values. Its <see cref="PasskeyRecord.CredentialId"/> identifies the passkey to update.</param>
    /// <param name="usedUtc">The date and time when the passkey was used (in UTC).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task UpdateAsync(PasskeyRecord passkey, DateTime usedUtc, CancellationToken cancellationToken);
}
