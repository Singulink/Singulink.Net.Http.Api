using System.Net;
using Singulink.Enums;

namespace Singulink.Net.Http.Api.Service.Testing;

/// <summary>
/// A session context for testing code that takes one, without a request or a session store. It has the token of a session or none, and checks the
/// request's origin and user ID precondition as session handling does. Signing in calls the session creation function with a <see cref="SignInInfo"/>
/// for the expiries of <see cref="Options"/>, and keeps the token that the function returns.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
/// <remarks>
/// The session is not validated against a session store: <see cref="SessionAccessOptions.ForceValidate"/> has no effect, and the token is never replaced
/// by one from a store.
/// </remarks>
public class TestSessionContext<TSessionToken> : SessionContext<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// The device of a test session context that is created without one.
    /// </summary>
    public const string DefaultDevice = "Test Device";

    /// <summary>
    /// Initializes a new instance of the <see cref="TestSessionContext{TSessionToken}"/> class.
    /// </summary>
    /// <param name="token">The token of the request's session, or <see langword="null"/> for a request without a session. The request's user ID
    /// precondition (see <see cref="UserIdPrecondition"/>) is the token's user ID.</param>
    /// <param name="device">The device making the request, or <see langword="null"/> for <see cref="DefaultDevice"/>.</param>
    /// <param name="ipAddress">The IP address of the device making the request, or <see langword="null"/> if it is not known.</param>
    public TestSessionContext(TSessionToken? token = null, string? device = null, IPAddress? ipAddress = null)
    {
        Token = token;
        UserIdPrecondition = token?.UserId;
        Device = device ?? DefaultDevice;
        IpAddress = ipAddress;
    }

    /// <inheritdoc/>
    public override string Device { get; }

    /// <inheritdoc/>
    public override IPAddress? IpAddress { get; }

    /// <summary>
    /// Gets or sets the token of the request's session, or <see langword="null"/> if the request has no session.
    /// </summary>
    public TSessionToken? Token { get; set; }

    /// <summary>
    /// Gets or sets the user ID precondition of the request, or <see langword="null"/> for a request without one.
    /// </summary>
    public string? UserIdPrecondition { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the request's origin is allowed. Default is <see langword="true"/>.
    /// </summary>
    public bool IsOriginAllowed { get; set; } = true;

    /// <summary>
    /// Gets the session handling options: the forced access options of token retrievals, and the expiries and maximum lifetimes of sign-ins.
    /// </summary>
    public SessionHandlingOptions Options { get; init; } = new();

    /// <summary>
    /// Gets the clock of sign-ins. Default is <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Gets the sign-in info that the session creation function of the last sign-in received, or <see langword="null"/> if there was no sign-in.
    /// </summary>
    public SignInInfo? LastSignIn { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the session was signed out with <see cref="SignOutAsync"/>.
    /// </summary>
    public bool IsSignedOut { get; private set; }

    /// <inheritdoc/>
    public override async ValueTask<TSessionToken> GetRequiredTokenAsync(SessionAccessOptions sessionOptions = default)
    {
        return await GetTokenAsync(sessionOptions) ?? throw SessionErrors.SignInRequired();
    }

    /// <inheritdoc/>
    public override ValueTask<TSessionToken?> GetTokenAsync(SessionAccessOptions sessionOptions = default)
    {
        sessionOptions |= Options.ForcedAccessOptions;
        EnsureOriginAllowed(sessionOptions);

        if (Token is { } token)
            ValidateUserIdPrecondition(token, sessionOptions.HasAllFlags(SessionAccessOptions.OptionalUserIdPrecondition));

        return ValueTask.FromResult(Token);
    }

    /// <inheritdoc/>
    public override async Task<TSessionToken> SignInAsync(bool persistent, Func<SignInInfo, Task<TSessionToken>> createSessionFunc)
    {
        EnsureSignInOriginAllowed();

        // As in session handling: a session that starts now is within its maximum lifetime, which caps its validity period if it is shorter.
        var utcNow = TimeProvider.GetUtcNow().UtcDateTime;
        var validFor = Options.GetValidFor(persistent, createdUtc: utcNow, utcNow)!.Value;

        var signInInfo = new SignInInfo(Device, IpAddress, utcNow, SessionTime.Add(utcNow, validFor), persistent);

        LastSignIn = signInInfo;
        Token = await createSessionFunc(signInInfo);
        IsSignedOut = false;

        return Token;
    }

    /// <inheritdoc/>
    public override Task SignOutAsync()
    {
        EnsureOriginAllowed(Options.ForcedAccessOptions);

        if (Token is { } token)
            ValidateUserIdPrecondition(token, optional: true);

        Token = null;
        IsSignedOut = true;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override void SetToken(TSessionToken sessionToken)
    {
        if (Token is null)
            throw SessionErrors.NoSessionToSetTokenOf();

        Token = sessionToken;
    }

    /// <inheritdoc/>
    public override void ClearToken() => Token = null;

    /// <inheritdoc/>
    protected override bool IsRequestOriginAllowed() => IsOriginAllowed;

    private void EnsureOriginAllowed(SessionAccessOptions sessionOptions)
    {
        if (!sessionOptions.HasAllFlags(SessionAccessOptions.AllowAllOrigins))
            EnsureSignInOriginAllowed();
    }

    private void ValidateUserIdPrecondition(TSessionToken token, bool optional)
    {
        string queryName = Options.UserIdPreconditionQueryName;

        if (UserIdPrecondition is not { } userId)
        {
            if (optional)
                return;

            throw SessionErrors.UserIdPreconditionMissing(queryName);
        }

        if (userId != token.UserId)
            throw SessionErrors.UserIdPreconditionMismatch(queryName, userId);
    }
}
