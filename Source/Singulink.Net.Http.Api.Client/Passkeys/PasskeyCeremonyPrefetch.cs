using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// Begins a passkey ceremony on the service ahead of the person's action that shows its prompt, such as a tap on a button, so that the prompt can follow
/// the action right away: browsers such as Safari only allow a WebAuthn operation shortly after a user action, and a request to the service in between
/// can take longer than that. A begun ceremony is kept for another try until its credential is sent to the service, which ends it, and one that is about
/// to expire is begun again. A credential whose sending failed for a reason that may not last is sent again on the next try instead of asking for another
/// passkey, unless it is discarded (see <see cref="DiscardUnsentCredential"/>).
/// </summary>
/// <typeparam name="TCeremony">The type of a begun ceremony: <see cref="PasskeyCeremony"/>, or a type of the app's own that holds one with what the app
/// needs to finish it, such as the <see cref="Pkce"/> pair that a sign-in is bound to.</typeparam>
/// <remarks>
/// <para>
/// A ceremony expires on the service at the end of its <see cref="PasskeyCeremony.Lifetime"/>, which the service counts from before it sent the
/// ceremony, and the credential has to reach the service before then. A ceremony is begun again once it is in the last fifth of its lifetime, counted
/// from when it was received.
/// </para>
/// <para>
/// Its members are not thread safe: a UI runs one try at a time.
/// </para>
/// </remarks>
public sealed class PasskeyCeremonyPrefetch<TCeremony>
{
    private readonly Func<CancellationToken, Task<TCeremony>> _begin;
    private readonly Func<TCeremony, PasskeyCeremony> _getCeremony;
    private readonly TimeProvider _timeProvider;

    // The ceremony that was begun for the next try, which completes with null if it failed to begin in the background.
    private Task<BegunCeremony?>? _ceremony;

    // A credential that may not have reached the service. A creation prompt that ran again would create a second passkey, and leave the first one in the
    // password manager without an account if its credential never arrived.
    private (BegunCeremony Ceremony, PasskeyCredential Credential)? _unsent;

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyCeremonyPrefetch{TCeremony}"/> class.
    /// </summary>
    /// <param name="begin">Begins a ceremony on the service.</param>
    /// <param name="getCeremony">Gets the <see cref="PasskeyCeremony"/> of a begun ceremony, such as <c>c =&gt; c</c> for a
    /// <see cref="PasskeyCeremony"/>.</param>
    /// <param name="timeProvider">The time provider that measures how long ago a ceremony was received. Default is <see langword="null"/>, which uses
    /// <see cref="TimeProvider.System"/>.</param>
    public PasskeyCeremonyPrefetch(
        Func<CancellationToken, Task<TCeremony>> begin, Func<TCeremony, PasskeyCeremony> getCeremony, TimeProvider? timeProvider = null)
    {
        _begin = begin;
        _getCeremony = getCeremony;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets a value indicating whether the last try (see <see cref="RunAsync{TResult}"/>) sent the credential of the try before it again, instead of
    /// showing the prompt. The service may have received that credential although its answer was lost, in which case it ended the ceremony, and the
    /// credential that was sent again fails as expired (<see cref="PasskeyErrorCodes.CeremonyExpired"/>).
    /// </summary>
    public bool ResentCredential { get; private set; }

    /// <summary>
    /// Begins a ceremony in the background, unless one that can still be used was begun or is being begun. A failure isn't reported: the next try begins
    /// the ceremony again, and reports its failure.
    /// </summary>
    public void Prefetch()
    {
        if (_ceremony is { IsCompleted: false })
            return;

        if (_ceremony is { Status: TaskStatus.RanToCompletion, Result: { } ceremony } && !IsExpired(ceremony))
            return;

        _ceremony = TryBeginAsync();
    }

    /// <summary>
    /// Shows the passkey prompt for the begun ceremony and sends the credential to the service. Begins the ceremony now if none was begun, it failed to
    /// begin or it is about to expire. Sends the credential of the last try again instead, without showing the prompt, if sending it failed for a reason
    /// that may not last (see <see cref="ApiFailures.IsTransient(Exception)"/>) or was cancelled, and its ceremony is not about to expire.
    /// </summary>
    /// <typeparam name="TResult">The type of the result of finishing the ceremony.</typeparam>
    /// <param name="prompt">Shows the passkey prompt with the WebAuthn options of the ceremony (<see cref="PasskeyCeremony.OptionsJson"/>) and returns the
    /// credential, such as <see cref="IPasskeyAuthenticator.GetCredentialAsync"/>.</param>
    /// <param name="finish">Sends the credential to the service to finish the ceremony.</param>
    /// <param name="cancellationToken">A token to cancel the try, which is passed to the delegates.</param>
    /// <remarks>
    /// A ceremony whose prompt fails or is cancelled is kept for the next try, or begun again in the background if it is about to expire. A ceremony whose
    /// credential was sent is not used again: sending it ends the ceremony on the service. When sending fails for another reason, a new ceremony is begun in
    /// the background for the next try.
    /// </remarks>
    /// <exception cref="PasskeyAuthenticatorException">The prompt failed or the person cancelled it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<TResult> RunAsync<TResult>(
        Func<string, CancellationToken, Task<PasskeyCredential>> prompt,
        Func<TCeremony, PasskeyCredential, CancellationToken, Task<TResult>> finish,
        CancellationToken cancellationToken = default)
    {
        BegunCeremony ceremony;
        PasskeyCredential? credential = null;
        ResentCredential = false;

        if (_unsent is { } unsent && !IsExpired(unsent.Ceremony))
        {
            (ceremony, credential) = unsent;
            ResentCredential = true;
        }
        else
        {
            ceremony = await GetCeremonyAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                credential = await prompt(ceremony.OptionsJson, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Without a credential, the ceremony is kept for another try, or begun again in the background if it is about to expire after the prompt.
                if (credential is null)
                    Prefetch();
            }
        }

        // Sending the credential ends the ceremony, so another try needs a new one.
        _ceremony = null;
        _unsent = null;

        try
        {
            return await finish(ceremony.Value, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ApiFailures.IsTransient(ex) || (ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            _unsent = (ceremony, credential);
            throw;
        }
        catch
        {
            Prefetch();
            throw;
        }
    }

    /// <inheritdoc cref="RunAsync{TResult}"/>
    public Task RunAsync(
        Func<string, CancellationToken, Task<PasskeyCredential>> prompt,
        Func<TCeremony, PasskeyCredential, CancellationToken, Task> finish,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(prompt, async (ceremony, credential, ct) => {
            await finish(ceremony, credential, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Discards the credential of the last try if sending it failed (see <see cref="RunAsync{TResult}"/>), so that the next try shows the passkey prompt
    /// again.
    /// </summary>
    public void DiscardUnsentCredential() => _unsent = null;

    private bool IsExpired(BegunCeremony ceremony) => _timeProvider.GetElapsedTime(ceremony.ReceivedTimestamp) > ceremony.UsableFor;

    /// <summary>
    /// Gets the begun ceremony, or begins one now if none was begun, it failed to begin or it is about to expire.
    /// </summary>
    private async Task<BegunCeremony> GetCeremonyAsync(CancellationToken cancellationToken)
    {
        if (_ceremony is { } begun && await begun.WaitAsync(cancellationToken).ConfigureAwait(false) is { } ceremony && !IsExpired(ceremony))
            return ceremony;

        var newCeremony = await BeginAsync(cancellationToken).ConfigureAwait(false);
        _ceremony = Task.FromResult<BegunCeremony?>(newCeremony);

        return newCeremony;
    }

    private async Task<BegunCeremony> BeginAsync(CancellationToken cancellationToken)
    {
        var value = await _begin(cancellationToken).ConfigureAwait(false);
        var ceremony = _getCeremony(value);

        // The service counts the lifetime from before it sent the ceremony, and the credential has to reach it in time.
        return new BegunCeremony(value, ceremony.OptionsJson, _timeProvider.GetTimestamp(), TimeSpan.FromTicks(ceremony.Lifetime.Ticks * 4 / 5));
    }

    private async Task<BegunCeremony?> TryBeginAsync()
    {
        try
        {
            return await BeginAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class BegunCeremony(TCeremony value, string optionsJson, long receivedTimestamp, TimeSpan usableFor)
    {
        public TCeremony Value { get; } = value;

        public string OptionsJson { get; } = optionsJson;

        public long ReceivedTimestamp { get; } = receivedTimestamp;

        public TimeSpan UsableFor { get; } = usableFor;
    }
}
