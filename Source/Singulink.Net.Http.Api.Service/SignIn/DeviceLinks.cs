using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// Signs in a new device by having the user approve it from a device where they are already signed in. The new device shows a short code, the user types it
/// on the signed-in device, checks the new device's details and approves it, and the new device then receives a one-time sign-in code.
/// </summary>
/// <remarks>
/// <para>
/// The flow has four steps:
/// </para>
/// <list type="number">
///   <item><description>The new device calls <see cref="StartAsync"/> with a PKCE code challenge and shows the returned code.</description></item>
///   <item><description>On a signed-in device, the user types the code, and <see cref="FindAsync(string, string, CancellationToken)"/> returns the new
///   device's details to check.</description></item>
///   <item><description>The user confirms, and <see cref="ApproveAsync(string, string, string?, CancellationToken)"/> approves the link for them.
///   </description></item>
///   <item><description>Meanwhile the new device checks the link's status with <see cref="CheckAsync"/> (or <see cref="PollAsync"/>). Once the link is
///   approved, the status contains a sign-in code, which the new device redeems with its code verifier (see
///   <see cref="SignInCodes.SignInAsync{T}(SessionContext{T}, RedeemSignInCodeCommand, Func{RedeemedSignInCode, SignInInfo, Task{T}}, CancellationToken)"/>).
///   </description></item>
/// </list>
/// <para>
/// Wrong codes count against the user typing them: after 5 wrong codes, <see cref="FindAsync(string, string, CancellationToken)"/> and
/// <see cref="ApproveAsync(string, string, string?, CancellationToken)"/> refuse further lookups for that user, so a signed-in account cannot be used to
/// guess codes. The count is forgotten once the link lifetime passes without a lookup by the user (lookups that are refused do not count), so refusals
/// have <see cref="ApiException.RetryAfter"/> set to the link lifetime. Input that cannot be a code (see <see cref="ShortCode.Normalize(string)"/>) is not
/// counted.
/// </para>
/// <para>
/// Links, wrong code counts and the sign-in codes are kept in the registered <see cref="IShortLivedStore"/>.
/// </para>
/// </remarks>
public sealed class DeviceLinks
{
    private const int MaxWrongCodes = 5;

    // Codes clash rarely, so a store that refuses this many new codes in a row is broken.
    private const int MaxCodeAttempts = 10;

    private static readonly DeviceLinkApprovalResult NotFound = new(DeviceLinkApprovalOutcome.NotFound, null);
    private static readonly DeviceLinkApprovalResult AlreadyApproved = new(DeviceLinkApprovalOutcome.AlreadyApproved, null);

    // How long after its code expires a link can still be polled, so that an approval in its last seconds reaches the new device.
    private static readonly TimeSpan PollGracePeriod = TimeSpan.FromMinutes(1);

    private readonly IShortLivedStore _store;
    private readonly SignInCodes _signInCodes;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceLinks"/> class.
    /// </summary>
    public DeviceLinks(
        IShortLivedStore store, SignInCodes signInCodes, IOptions<DeviceLinkOptions> options, TimeProvider timeProvider, ILogger<DeviceLinks> logger)
    {
        _store = store;
        _signInCodes = signInCodes;
        _timeProvider = timeProvider;
        _lifetime = options.Value.Lifetime;
        _logger = logger;
    }

    // The link and its approval outlive the code, so that the new device can collect an approval made just before the code expired.
    private TimeSpan PollableLifetime => _lifetime + PollGracePeriod;

    /// <summary>
    /// Starts a link for a new device and returns what the new device needs, which the app returns to it as-is: the code to show, the ID and poll secret
    /// to check the link's status, and how long the link waits for approval (<see cref="DeviceLinkOptions.Lifetime"/>).
    /// </summary>
    /// <param name="codeChallenge">The new device's <c>S256</c> code challenge (see <see cref="PkceChallenge"/>), which the sign-in code is bound to.</param>
    /// <param name="device">The new device, in the form of <see cref="SessionContext{TSessionToken}.Device"/>.</param>
    /// <param name="ipAddress">The new device's IP address, or <see langword="null"/> if it is not known.</param>
    /// <param name="location">The new device's approximate location to show to the approver, if the application can determine it.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="BadRequestApiException">The code challenge is malformed (<see cref="SignInErrorCodes.CodeChallengeInvalid"/>).</exception>
    public async Task<DeviceLinkStart> StartAsync(
        string codeChallenge, string device, IPAddress? ipAddress, string? location, CancellationToken cancellationToken = default)
    {
        PkceChallenge.EnsureValid(codeChallenge);

        var createdUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresUtc = createdUtc + _lifetime;
        string id = ShortLivedId.Create();
        string pollSecret = ShortLivedId.Create();
        var codeState = new CodeState(id, device, ipAddress?.ToString(), location, createdUtc, expiresUtc);
        string code = ShortCode.Generate();
        int attempts = 1;

        // Adding the code only if no other link has it keeps codes unique across servers.
        while (!await _store.TryAddAsync(ShortLivedKeys.DeviceLinkCode, code, codeState, SignInJsonContext.Default.CodeState, _lifetime, cancellationToken))
        {
            if (++attempts > MaxCodeAttempts)
                throw new InvalidOperationException("The short-lived store did not add a new device link code.");

            code = ShortCode.Generate();
        }

        // Once the code is added, the link is added whatever happens to the request: a code without its link would let a user approve a device that can
        // never collect the sign-in code. If the link cannot be added, the code is removed.
        var linkState = new LinkState(code, pollSecret, codeChallenge, expiresUtc);
        bool linkAdded = false;

        try
        {
            linkAdded = await _store.TryAddAsync(
                ShortLivedKeys.DeviceLink, id, linkState, SignInJsonContext.Default.LinkState, PollableLifetime, CancellationToken.None);
        }
        finally
        {
            if (!linkAdded)
                await _store.RemoveAsync(ShortLivedKeys.DeviceLinkCode + code, CancellationToken.None);
        }

        if (!linkAdded)
            throw new InvalidOperationException("The short-lived store did not add the device link under its new ID.");

        return new DeviceLinkStart(id, ShortCode.Format(code), pollSecret, _lifetime);
    }

    /// <summary>
    /// Finds the device waiting with a code typed by a signed-in user and returns its details, or <see langword="null"/> if no device is waiting with the
    /// code or the input cannot be a code.
    /// </summary>
    /// <param name="code">The code as typed by the user (see <see cref="ShortCode.Normalize(string)"/>).</param>
    /// <param name="approverUserId">The ID of the signed-in user who typed the code.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="TooManyRequestsApiException">The user entered too many wrong codes (<see cref="SignInErrorCodes.DeviceLinkTooManyWrongCodes"/>).
    /// </exception>
    public async Task<PendingDeviceLink?> FindAsync(string code, string approverUserId, CancellationToken cancellationToken = default)
    {
        if (await FindLinkAsync(code, approverUserId, cancellationToken) is not { } link)
            return null;

        return link.ToPendingDeviceLink();
    }

    /// <summary>
    /// Finds the device waiting with a code typed by a signed-in user and returns its details, or <see langword="null"/> if no device is waiting with the
    /// code or the input cannot be a code.
    /// </summary>
    /// <param name="code">The code as typed by the user (see <see cref="ShortCode.Normalize(string)"/>).</param>
    /// <param name="approver">The session token of the signed-in user who typed the code, whose <see cref="ISessionToken.UserId"/> identifies them.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="TooManyRequestsApiException">The user entered too many wrong codes (<see cref="SignInErrorCodes.DeviceLinkTooManyWrongCodes"/>).
    /// </exception>
    public Task<PendingDeviceLink?> FindAsync(string code, ISessionToken approver, CancellationToken cancellationToken = default)
    {
        return FindAsync(code, approver.UserId, cancellationToken);
    }

    /// <summary>
    /// Approves the device waiting with a code typed by a signed-in user, so that the device is signed in to that user's account, and returns the outcome
    /// with the details of the device that was approved. A link can only be approved once.
    /// </summary>
    /// <param name="code">The code as typed by the user (see <see cref="ShortCode.Normalize(string)"/>).</param>
    /// <param name="approverUserId">The ID of the signed-in user who typed the code and whom the new device will be signed in as.</param>
    /// <param name="appData">An optional value of the app's that the sign-in code issued for the link carries (see <see cref="RedeemedSignInCode.AppData"/>).
    /// It is kept in the short-lived store with the approval.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="TooManyRequestsApiException">The user entered too many wrong codes (<see cref="SignInErrorCodes.DeviceLinkTooManyWrongCodes"/>).
    /// </exception>
    public async Task<DeviceLinkApprovalResult> ApproveAsync(
        string code, string approverUserId, string? appData = null, CancellationToken cancellationToken = default)
    {
        if (await FindLinkAsync(code, approverUserId, cancellationToken) is not { } link)
            return NotFound;

        if (link.ExpiresUtc <= _timeProvider.GetUtcNow().UtcDateTime)
            return NotFound;

        // The first approval to be added wins, on whichever server it runs. It is added after the code and the link and kept as long as the link, so it
        // outlives the code whatever the servers' clocks say, and no one else can approve the link while its code can still be looked up.
        var approval = new ApprovalState(approverUserId, appData);
        var approvalTypeInfo = SignInJsonContext.Default.ApprovalState;
        bool approved = await _store.TryAddAsync(ShortLivedKeys.DeviceLinkApproval, link.Id, approval, approvalTypeInfo, PollableLifetime, cancellationToken);

        return approved ? new DeviceLinkApprovalResult(DeviceLinkApprovalOutcome.Approved, link.ToPendingDeviceLink()) : AlreadyApproved;
    }

    /// <summary>
    /// Approves the device waiting with a code typed by a signed-in user, so that the device is signed in to that user's account, and returns the outcome
    /// with the details of the device that was approved. A link can only be approved once.
    /// </summary>
    /// <param name="code">The code as typed by the user (see <see cref="ShortCode.Normalize(string)"/>).</param>
    /// <param name="approver">The session token of the signed-in user who typed the code and whom the new device will be signed in as, whose
    /// <see cref="ISessionToken.UserId"/> identifies them.</param>
    /// <param name="appData">An optional value of the app's that the sign-in code issued for the link carries (see <see cref="RedeemedSignInCode.AppData"/>).
    /// It is kept in the short-lived store with the approval.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="TooManyRequestsApiException">The user entered too many wrong codes (<see cref="SignInErrorCodes.DeviceLinkTooManyWrongCodes"/>).
    /// </exception>
    public Task<DeviceLinkApprovalResult> ApproveAsync(
        string code, ISessionToken approver, string? appData = null, CancellationToken cancellationToken = default)
    {
        return ApproveAsync(code, approver.UserId, appData, cancellationToken);
    }

    /// <summary>
    /// Gets the status of a link for the new device, or returns <see langword="null"/> if the link is unknown or expired, or the poll secret is wrong. Once
    /// the link is approved, the status contains a one-time sign-in code bound to the link's code challenge.
    /// </summary>
    /// <param name="id">The link ID from <see cref="DeviceLinkStart.Id"/>.</param>
    /// <param name="pollSecret">The poll secret from <see cref="DeviceLinkStart.PollSecret"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// Every poll after approval gets the same sign-in code, so the new device can poll again if a response does not reach it. The code can be redeemed
    /// once, and only with the new device's code verifier. It is valid for as long as the link can be polled, and for at least the lifetime of sign-in
    /// codes (see <see cref="SignInCodeOptions.Lifetime"/>).
    /// </remarks>
    public async Task<DeviceLinkPollResult?> PollAsync(string id, string pollSecret, CancellationToken cancellationToken = default)
    {
        if (!ShortLivedId.IsWellFormed(id))
            return null;

        var link = await _store.GetAsync(ShortLivedKeys.DeviceLink, id, SignInJsonContext.Default.LinkState, _logger, cancellationToken);

        if (link is null || !ShortLivedId.FixedTimeEquals(link.PollSecret, pollSecret))
            return null;

        var approvalTypeInfo = SignInJsonContext.Default.ApprovalState;

        if (await _store.GetAsync(ShortLivedKeys.DeviceLinkApproval, id, approvalTypeInfo, _logger, cancellationToken) is not { } approval)
            return link.ExpiresUtc > _timeProvider.GetUtcNow().UtcDateTime ? DeviceLinkPollResult.Pending : null;

        var issued = await _store.GetAsync(ShortLivedKeys.DeviceLinkSignInCode, id, SignInJsonContext.Default.IssuedState, _logger, cancellationToken);

        if (issued is null)
        {
            // Every poll returns the code, so it is valid for as long as the link can be polled, and for at least the lifetime of sign-in codes.
            var codeLifetime = link.ExpiresUtc + PollGracePeriod - _timeProvider.GetUtcNow().UtcDateTime;

            if (codeLifetime < _signInCodes.Lifetime)
                codeLifetime = _signInCodes.Lifetime;

            // From here on the request's cancellation no longer applies: the sign-in code is issued and kept, or the next poll issues one.
            string signInCode = await _signInCodes.CreateWithLifetimeAsync(
                approval.ApproverUserId, link.CodeChallenge, approval.AppData, codeLifetime, CancellationToken.None);

            issued = new IssuedState(signInCode, approval.ApproverUserId);

            // Concurrent polls race to keep their code, and all of them return the one that was kept.
            var issuedTypeInfo = SignInJsonContext.Default.IssuedState;

            if (!await _store.TryAddAsync(ShortLivedKeys.DeviceLinkSignInCode, id, issued, issuedTypeInfo, PollableLifetime, CancellationToken.None))
                issued = await _store.GetAsync(ShortLivedKeys.DeviceLinkSignInCode, id, issuedTypeInfo, _logger, CancellationToken.None);

            // The link can no longer be looked up by its code.
            await _store.RemoveAsync(ShortLivedKeys.DeviceLinkCode + link.Code, CancellationToken.None);
        }

        return issued is null ? null : new DeviceLinkPollResult(issued.SignInCode, issued.ApproverUserId);
    }

    /// <summary>
    /// Gets the status of a link for the new device, which the app returns to it as-is. Once the link is approved, the status contains a one-time sign-in
    /// code bound to the link's code challenge.
    /// </summary>
    /// <param name="id">The link ID from <see cref="DeviceLinkStart.Id"/>.</param>
    /// <param name="pollSecret">The poll secret from <see cref="DeviceLinkStart.PollSecret"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// <inheritdoc cref="PollAsync" path="/remarks"/>
    /// </remarks>
    /// <exception cref="UnauthorizedApiException">The link is unknown or expired, or the poll secret is wrong
    /// (<see cref="SignInErrorCodes.DeviceLinkExpired"/>).</exception>
    public async Task<DeviceLinkStatus> CheckAsync(string id, string pollSecret, CancellationToken cancellationToken = default)
    {
        var result = await PollAsync(id, pollSecret, cancellationToken) ??
            throw new UnauthorizedApiException("The code expired before it was approved. Please start again.") {
                ErrorCode = SignInErrorCodes.DeviceLinkExpired,
            };

        return new DeviceLinkStatus(result.SignInCode);
    }

    private async Task<CodeState?> FindLinkAsync(string code, string approverUserId, CancellationToken cancellationToken)
    {
        string wrongCodesKey = ShortLivedKeys.DeviceLinkWrongCodes + ShortLivedId.Hash(approverUserId);

        if (await GetCountAsync(wrongCodesKey, cancellationToken) >= MaxWrongCodes)
            throw TooManyWrongCodes();

        // Input that cannot be a code is not a guess, so it does not count against the user.
        if (ShortCode.Normalize(code) is not { } normalizedCode)
            return null;

        // Each lookup is counted as a wrong code before it is made, so that concurrent lookups cannot get past the limit, and given back unless the code
        // turns out to be wrong. Every count sets the counter to expire the link lifetime later. Once counted, the lookup goes ahead whatever happens to
        // the request, so that a right code is always given back.
        if (await _store.IncrementAsync(wrongCodesKey, 1, _lifetime, cancellationToken) > MaxWrongCodes)
        {
            await _store.IncrementAsync(wrongCodesKey, -1, _lifetime, CancellationToken.None);
            throw TooManyWrongCodes();
        }

        bool wrongCode = false;

        try
        {
            var codeTypeInfo = SignInJsonContext.Default.CodeState;
            var link = await _store.GetAsync(ShortLivedKeys.DeviceLinkCode, normalizedCode, codeTypeInfo, _logger, CancellationToken.None);
            wrongCode = link is null;

            return link;
        }
        finally
        {
            // A lookup that failed tells nothing about the code, so it is given back too.
            if (!wrongCode)
                await _store.IncrementAsync(wrongCodesKey, -1, _lifetime, CancellationToken.None);
        }
    }

    private async Task<long> GetCountAsync(string key, CancellationToken cancellationToken)
    {
        string? value = await _store.GetAsync(key, cancellationToken);
        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long count) ? count : 0;
    }

    // Every counted lookup sets the count to expire a link lifetime later and refused lookups are not counted, so the user can look up codes again a link
    // lifetime from now at the latest.
    private TooManyRequestsApiException TooManyWrongCodes() => new("Too many wrong codes. Please wait a few minutes and try again.") {
        ErrorCode = SignInErrorCodes.DeviceLinkTooManyWrongCodes,
        RetryAfter = _lifetime,
    };

    /// <summary>
    /// The part of a link that the new device polls, kept under the link ID.
    /// </summary>
    internal sealed record LinkState(string Code, string PollSecret, string CodeChallenge, DateTime ExpiresUtc);

    /// <summary>
    /// The part of a link that the approver looks up, kept under the code.
    /// </summary>
    internal sealed record CodeState(string Id, string Device, string? IpAddress, string? Location, DateTime CreatedUtc, DateTime ExpiresUtc)
    {
        public PendingDeviceLink ToPendingDeviceLink() => new(Device, IpAddress, Location, CreatedUtc);
    }

    /// <summary>
    /// The approval of a link, kept under the link ID.
    /// </summary>
    internal sealed record ApprovalState(
        string ApproverUserId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppData = null);

    /// <summary>
    /// The sign-in code issued for an approved link, kept under the link ID so that every poll gets the same code.
    /// </summary>
    internal sealed record IssuedState(string SignInCode, string ApproverUserId);
}
