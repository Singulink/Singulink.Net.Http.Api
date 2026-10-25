using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Waits on a new device for the approval of a device link that it started, by checking the link's status until it has a sign-in code. The link's
/// <see cref="DeviceLinkStart.Lifetime"/> is counted from when the waiter was created, which doesn't depend on the device's clock.
/// </summary>
/// <remarks>
/// <para>
/// The service decides when the link has expired: a check of a link that is unknown or expired fails with an <see cref="UnauthorizedApiException"/> with
/// the <see cref="SignInErrorCodes.DeviceLinkExpired"/> error code, which ends the wait. A check that fails for a reason that may not last (see
/// <see cref="ApiFailures.IsTransient(Exception)"/>), such as while the service can't be reached, is made again after the poll interval, until the link's
/// lifetime has passed.
/// </para>
/// <para>
/// Its members are not thread safe.
/// </para>
/// </remarks>
public sealed class DeviceLinkWaiter
{
    private readonly TimeProvider _timeProvider;
    private readonly long _receivedTimestamp;
    private TimeSpan _pollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceLinkWaiter"/> class for the specified link, which the link's lifetime is counted from.
    /// </summary>
    /// <param name="link">The link that the service started.</param>
    /// <param name="timeProvider">The time provider that measures how long ago the link was received and the poll interval. Default is
    /// <see langword="null"/>, which uses <see cref="TimeProvider.System"/>.</param>
    public DeviceLinkWaiter(DeviceLinkStart link, TimeProvider? timeProvider = null)
    {
        Link = link;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _receivedTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Gets the link that the waiter waits for, whose <see cref="DeviceLinkStart.Code"/> the new device shows.
    /// </summary>
    public DeviceLinkStart Link { get; }

    /// <summary>
    /// Gets how much of the link's <see cref="DeviceLinkStart.Lifetime"/> is left, counted from when the waiter was created with the waiter's time
    /// provider, or <see cref="TimeSpan.Zero"/> once it has passed.
    /// </summary>
    /// <remarks>
    /// The link's lifetime started on the service before the waiter was created, so on the service it ends earlier, by the time the link took to reach
    /// the device.
    /// </remarks>
    public TimeSpan RemainingLifetime
    {
        get {
            var remaining = Link.Lifetime - _timeProvider.GetElapsedTime(_receivedTimestamp);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Gets or sets how long the waiter waits before each check of the link's status. Default is 2 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan PollInterval
    {
        get => _pollInterval;
        set {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The poll interval must be positive.");

            _pollInterval = value;
        }
    }

    /// <summary>
    /// Checks the link's status after each poll interval until the link is approved, and returns the sign-in code that the status then has, which the
    /// new device redeems with the code verifier that the link was started with.
    /// </summary>
    /// <param name="checkStatus">Checks the link's status with the service, such as with the app's endpoint that calls <c>DeviceLinks.CheckAsync</c>,
    /// with a command that has the link's ID and poll secret.</param>
    /// <param name="reachabilityChanged">An optional callback that is called with <see langword="false"/> when a check fails for a reason that may not
    /// last, and with <see langword="true"/> when a check succeeds again after that.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <exception cref="UnauthorizedApiException">The link is unknown or has expired, with the <see cref="SignInErrorCodes.DeviceLinkExpired"/> error code.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// A check that fails for a reason that may not last is made again, unless the link's lifetime has passed, in which case its exception is thrown. Other
    /// exceptions of a check are thrown as they are.
    /// </remarks>
    public async Task<string> WaitForApprovalAsync(
        Func<CheckDeviceLinkCommand, CancellationToken, Task<DeviceLinkStatus>> checkStatus,
        Action<bool>? reachabilityChanged = null,
        CancellationToken cancellationToken = default)
    {
        var command = new CheckDeviceLinkCommand(Link.Id, Link.PollSecret);
        bool reachable = true;

        while (true)
        {
#if NET
            await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
#else
            await _timeProvider.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
#endif
            DeviceLinkStatus status;

            try
            {
                status = await checkStatus(command, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ApiFailures.IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                // The service ends the link once it expires, so its clock decides. The lifetime measured here only ends a wait in which the service can't
                // be reached.
                if (RemainingLifetime == TimeSpan.Zero)
                    throw;

                if (reachable)
                {
                    reachable = false;
                    reachabilityChanged?.Invoke(false);
                }

                continue;
            }

            if (!reachable)
            {
                reachable = true;
                reachabilityChanged?.Invoke(true);
            }

            if (status.SignInCode is { } signInCode)
                return signInCode;
        }
    }
}
