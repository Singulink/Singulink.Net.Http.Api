using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// Creates and redeems one-time sign-in codes. A code is bound to a PKCE code challenge, so only the holder of the matching code verifier can exchange it
/// for a session, wherever the code travels (a redirect to an app, a device link or a response that could be intercepted).
/// </summary>
/// <remarks>
/// <para>
/// Every way of signing in can end the same way: the client sends a code challenge when it starts signing in, the step that verifies the user (a passkey, a
/// password, an approved device link) creates a code bound to that challenge with <see cref="CreateAsync"/> and returns it, and the client exchanges it for
/// a session with its code verifier through an endpoint that calls
/// <see cref="SignInAsync{T}(SessionContext{T}, RedeemSignInCodeCommand, Func{RedeemedSignInCode, SignInInfo, Task{T}}, CancellationToken)"/>.
/// </para>
/// <para>
/// Codes are kept in the registered <see cref="IShortLivedStore"/>.
/// </para>
/// </remarks>
public sealed class SignInCodes
{
    private readonly IShortLivedStore _store;
    private readonly TimeSpan _lifetime;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SignInCodes"/> class.
    /// </summary>
    public SignInCodes(IShortLivedStore store, IOptions<SignInCodeOptions> options, ILogger<SignInCodes> logger)
    {
        _store = store;
        _lifetime = options.Value.Lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Creates a one-time sign-in code for a user, bound to the specified PKCE code challenge.
    /// </summary>
    /// <param name="userId">The ID of the user that the code signs in.</param>
    /// <param name="codeChallenge">The client's <c>S256</c> code challenge (see <see cref="PkceChallenge"/>), from the request that started the sign-in.
    /// </param>
    /// <param name="appData">An optional value of the app's that the code carries and that redeeming it returns (see
    /// <see cref="RedeemedSignInCode.AppData"/>). It is kept in the short-lived store with the code.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="BadRequestApiException">The code challenge is malformed (<see cref="SignInErrorCodes.CodeChallengeInvalid"/>).</exception>
    public Task<string> CreateAsync(string userId, string codeChallenge, string? appData = null, CancellationToken cancellationToken = default)
    {
        return CreateWithLifetimeAsync(userId, codeChallenge, appData, _lifetime, cancellationToken);
    }

    /// <summary>
    /// Redeems a sign-in code and returns the ID of the user it signs in and the app's value it was created with, or <see langword="null"/> if the code is
    /// unknown, expired or already used, or the code verifier does not match the code's challenge.
    /// </summary>
    /// <param name="code">The sign-in code.</param>
    /// <param name="codeVerifier">The PKCE code verifier that matches the challenge the code was created with.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// Redeeming uses the code up whether or not the verifier matches, so a code that reached the wrong party can be tried only once.
    /// </remarks>
    public async Task<RedeemedSignInCode?> RedeemAsync(string code, string codeVerifier, CancellationToken cancellationToken = default)
    {
        if (!ShortLivedId.IsWellFormed(code))
            return null;

        var state = await _store.TakeAsync(ShortLivedKeys.SignInCode, code, SignInJsonContext.Default.SignInCodeState, _logger, cancellationToken);

        if (state is null || !PkceChallenge.IsMatch(state.CodeChallenge, codeVerifier))
            return null;

        return new RedeemedSignInCode(state.UserId, state.AppData);
    }

    /// <summary>
    /// Signs in the user of a one-time sign-in code: checks the request's origin with
    /// <see cref="SessionContext{TSessionToken}.EnsureSignInOriginAllowed"/>, redeems the code with <see cref="RedeemAsync"/> and then signs in with
    /// <see cref="SessionContext{TSessionToken}.SignInAsync(bool, Func{SignInInfo, Task{TSessionToken}})"/>.
    /// </summary>
    /// <param name="sessionContext">The session context of the current request.</param>
    /// <param name="code">The sign-in code.</param>
    /// <param name="codeVerifier">The PKCE code verifier that matches the challenge the code was created with.</param>
    /// <param name="persistent">If set to <see langword="true"/>, the session will persist across browser or application restarts.</param>
    /// <param name="createSessionFunc">A function that creates a new session for the user of the redeemed code (see <see cref="RedeemedSignInCode"/>,
    /// which also has the app's value the code was created with) based on the provided <see cref="SignInInfo"/>. It is only called if the code is valid.
    /// </param>
    /// <param name="cancellationToken">A token to cancel redeeming the code.</param>
    /// <exception cref="UnauthorizedApiException">The code is unknown, expired or already used, or the code verifier does not match
    /// (<see cref="SignInErrorCodes.SignInCodeInvalid"/>).</exception>
    /// <exception cref="ForbiddenApiException">The request's origin is not allowed (<see cref="ApiErrorCodes.OriginBlocked"/>). The code is not redeemed.
    /// </exception>
    public async Task<TSessionToken> SignInAsync<TSessionToken>(
        SessionContext<TSessionToken> sessionContext,
        string code,
        string codeVerifier,
        bool persistent,
        Func<RedeemedSignInCode, SignInInfo, Task<TSessionToken>> createSessionFunc,
        CancellationToken cancellationToken = default)
        where TSessionToken : class, ISessionToken
    {
        // Redeeming uses the code up, so a request that signing in would reject must not get that far.
        sessionContext.EnsureSignInOriginAllowed();

        var redeemed = await RedeemAsync(code, codeVerifier, cancellationToken) ??
            throw new UnauthorizedApiException("The sign-in code is invalid or expired. Please sign in again.") {
                ErrorCode = SignInErrorCodes.SignInCodeInvalid,
            };

        return await sessionContext.SignInAsync(persistent, info => createSessionFunc(redeemed, info));
    }

    /// <summary>
    /// Signs in the user of a one-time sign-in code with the values of a <see cref="RedeemSignInCodeCommand"/>: checks the request's origin with
    /// <see cref="SessionContext{TSessionToken}.EnsureSignInOriginAllowed"/>, redeems the code with <see cref="RedeemAsync"/> and then signs in with
    /// <see cref="SessionContext{TSessionToken}.SignInAsync(bool, Func{SignInInfo, Task{TSessionToken}})"/>.
    /// </summary>
    /// <param name="sessionContext">The session context of the current request.</param>
    /// <param name="command">The code, the code verifier and whether the session is persistent.</param>
    /// <param name="createSessionFunc">A function that creates a new session for the user of the redeemed code (see <see cref="RedeemedSignInCode"/>,
    /// which also has the app's value the code was created with) based on the provided <see cref="SignInInfo"/>. It is only called if the code is valid.
    /// </param>
    /// <param name="cancellationToken">A token to cancel redeeming the code.</param>
    /// <exception cref="UnauthorizedApiException">The code is unknown, expired or already used, or the code verifier does not match
    /// (<see cref="SignInErrorCodes.SignInCodeInvalid"/>).</exception>
    /// <exception cref="ForbiddenApiException">The request's origin is not allowed (<see cref="ApiErrorCodes.OriginBlocked"/>). The code is not redeemed.
    /// </exception>
    public Task<TSessionToken> SignInAsync<TSessionToken>(
        SessionContext<TSessionToken> sessionContext,
        RedeemSignInCodeCommand command,
        Func<RedeemedSignInCode, SignInInfo, Task<TSessionToken>> createSessionFunc,
        CancellationToken cancellationToken = default)
        where TSessionToken : class, ISessionToken
    {
        return SignInAsync(sessionContext, command.Code, command.CodeVerifier, command.IsPersistent, createSessionFunc, cancellationToken);
    }

    /// <summary>
    /// Gets how long a sign-in code can be redeemed after it is created, unless it is created with a lifetime of its own.
    /// </summary>
    internal TimeSpan Lifetime => _lifetime;

    /// <summary>
    /// Creates a one-time sign-in code for a user that can be redeemed for the specified time.
    /// </summary>
    internal async Task<string> CreateWithLifetimeAsync(
        string userId, string codeChallenge, string? appData, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        PkceChallenge.EnsureValid(codeChallenge);

        var state = new SignInCodeState(userId, codeChallenge, appData);
        return await _store.AddAsync(ShortLivedKeys.SignInCode, state, SignInJsonContext.Default.SignInCodeState, lifetime, cancellationToken);
    }

    internal sealed record SignInCodeState(
        string UserId,
        string CodeChallenge,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppData = null);
}
