using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for configuring ASP.NET Core rate limiting (<see cref="RateLimiterOptions"/>) with per-client limits and API error responses.
/// </summary>
public static class RateLimiterOptionsExtensions
{
    private const string DefaultRejectionMessage = "Too many requests. Please wait a moment and try again.";

    /// <summary>
    /// Adds a configuration that applies to the per-client limits that are added to the options after it (see
    /// <see cref="AddClientFixedWindowLimiter(RateLimiterOptions, string, Action{ClientRateLimitOptions})"/> and
    /// <see cref="SetClientFixedWindowGlobalLimiter(RateLimiterOptions, Action{ClientRateLimitOptions})"/>), before the configuration of each limit. Several
    /// configurations run in the order they are added.
    /// </summary>
    /// <param name="options">The rate limiter options.</param>
    /// <param name="configure">An action to configure the options of every limit, such as binding them from a configuration section.</param>
    public static RateLimiterOptions ConfigureClientLimits(this RateLimiterOptions options, Action<ClientRateLimitOptions> configure)
    {
        ClientRateLimits.Get(options).AddSharedConfiguration(configure);
        return options;
    }

    /// <summary>
    /// Adds a rate limiting policy that allows each client address a fixed number of requests per window. Apply it to endpoints with
    /// <see cref="RateLimiterEndpointConventionBuilderExtensions.RequireRateLimiting{TBuilder}(TBuilder, string)"/>.
    /// </summary>
    /// <param name="options">The rate limiter options.</param>
    /// <param name="policyName">The name of the policy.</param>
    /// <param name="permitLimit">The number of requests each client can make in each window (see <see cref="ClientRateLimitOptions.PermitLimit"/>).</param>
    /// <param name="window">The length of a window (see <see cref="ClientRateLimitOptions.Window"/>).</param>
    /// <remarks>
    /// <inheritdoc cref="AddClientFixedWindowLimiter(RateLimiterOptions, string, Action{ClientRateLimitOptions})" path="/remarks"/>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="permitLimit"/> or <paramref name="window"/> is zero or negative.</exception>
    /// <exception cref="FormatException">An entry in <see cref="ClientRateLimitOptions.ExemptNetworks"/> is not a valid address or network.</exception>
    public static RateLimiterOptions AddClientFixedWindowLimiter(this RateLimiterOptions options, string policyName, int permitLimit, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(permitLimit, 0);

        return options.AddClientFixedWindowLimiter(policyName, o => {
            o.PermitLimit = permitLimit;
            o.Window = window;
        });
    }

    /// <summary>
    /// Adds a rate limiting policy that allows each client address a fixed number of requests per window. Apply it to endpoints with
    /// <see cref="RateLimiterEndpointConventionBuilderExtensions.RequireRateLimiting{TBuilder}(TBuilder, string)"/>.
    /// </summary>
    /// <param name="options">The rate limiter options.</param>
    /// <param name="policyName">The name of the policy.</param>
    /// <param name="configure">An action to configure the limit. <see cref="ClientRateLimitOptions.PermitLimit"/> must be set.</param>
    /// <remarks>
    /// <para>
    /// See <see cref="ClientRateLimitOptions"/> for how clients are identified and which clients are exempt. The configurations added with
    /// <see cref="ConfigureClientLimits"/> before this call run before <paramref name="configure"/>.
    /// </para>
    /// <para>
    /// If <see cref="RateLimiterOptions.OnRejected"/> is not set, this also sets the rejection response with
    /// <see cref="SetApiRejectionResponse(RateLimiterOptions, string)"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><see cref="ClientRateLimitOptions.PermitLimit"/> was not set.</exception>
    /// <exception cref="FormatException">An entry in <see cref="ClientRateLimitOptions.ExemptNetworks"/> is not a valid address or network.</exception>
    public static RateLimiterOptions AddClientFixedWindowLimiter(this RateLimiterOptions options, string policyName, Action<ClientRateLimitOptions> configure)
    {
        var partitioner = ClientRateLimitPartitioner.Create(options, configure, $"{ShortLivedKeys.RateLimit}{policyName}:", isGlobal: false);
        SetDefaultRejectionResponse(options);

        return options.AddPolicy(policyName, partitioner.GetPartition);
    }

    /// <summary>
    /// Sets the global limiter, which applies to every request, to a limiter that allows each client address a fixed number of requests per window.
    /// </summary>
    /// <param name="options">The rate limiter options.</param>
    /// <param name="permitLimit">The number of requests each client can make in each window (see <see cref="ClientRateLimitOptions.PermitLimit"/>).</param>
    /// <param name="window">The length of a window (see <see cref="ClientRateLimitOptions.Window"/>).</param>
    /// <remarks>
    /// <inheritdoc cref="SetClientFixedWindowGlobalLimiter(RateLimiterOptions, Action{ClientRateLimitOptions})" path="/remarks"/>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="permitLimit"/> or <paramref name="window"/> is zero or negative.</exception>
    /// <exception cref="FormatException">An entry in <see cref="ClientRateLimitOptions.ExemptNetworks"/> is not a valid address or network.</exception>
    public static RateLimiterOptions SetClientFixedWindowGlobalLimiter(this RateLimiterOptions options, int permitLimit, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(permitLimit, 0);

        return options.SetClientFixedWindowGlobalLimiter(o => {
            o.PermitLimit = permitLimit;
            o.Window = window;
        });
    }

    /// <summary>
    /// Sets the global limiter, which applies to every request, to a limiter that allows each client address a fixed number of requests per window.
    /// </summary>
    /// <param name="options">The rate limiter options.</param>
    /// <param name="configure">An action to configure the limit. <see cref="ClientRateLimitOptions.PermitLimit"/> must be set.</param>
    /// <remarks>
    /// <para>
    /// See <see cref="ClientRateLimitOptions"/> for how clients are identified and which clients are exempt. The configurations added with
    /// <see cref="ConfigureClientLimits"/> before this call run before <paramref name="configure"/>. Endpoint policies (see
    /// <see cref="AddClientFixedWindowLimiter(RateLimiterOptions, string, Action{ClientRateLimitOptions})"/>) apply in addition to the global limiter.
    /// </para>
    /// <para>
    /// If <see cref="RateLimiterOptions.OnRejected"/> is not set, this also sets the rejection response with
    /// <see cref="SetApiRejectionResponse(RateLimiterOptions, string)"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><see cref="ClientRateLimitOptions.PermitLimit"/> was not set.</exception>
    /// <exception cref="FormatException">An entry in <see cref="ClientRateLimitOptions.ExemptNetworks"/> is not a valid address or network.</exception>
    public static RateLimiterOptions SetClientFixedWindowGlobalLimiter(this RateLimiterOptions options, Action<ClientRateLimitOptions> configure)
    {
        var partitioner = ClientRateLimitPartitioner.Create(options, configure, ShortLivedKeys.GlobalRateLimit, isGlobal: true);
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, IPAddress>(partitioner.GetPartition);
        SetDefaultRejectionResponse(options);

        return options;
    }

    /// <summary>
    /// Configures rejected requests to receive a <see cref="TooManyRequestsApiException"/> response in the toolkit's error format, with the
    /// <see cref="ApiErrorCodes.RateLimited"/> error code, so that clients built with the toolkit report it like any other API error. A request that a
    /// per-client limit with a <see cref="ClientRateLimitOptions.RejectionMessage"/> rejects receives that message.
    /// <see cref="ApiException.RetryAfter"/> is set when the limiter reports how long the client should wait, which for the per-client limits is the time
    /// until the next window starts. A request that a per-client limit could not count because the short-lived store threw a <see
    /// cref="ServiceUnavailableApiException"/>, such as a full <see cref="MemoryShortLivedStore"/>, receives that exception instead.
    /// </summary>
    /// <param name="options">The rate limiter options.</param>
    /// <param name="message">The message reported to the client for the limits without a message of their own, or <see langword="null"/> to use a default
    /// message.</param>
    /// <remarks>
    /// This replaces <see cref="RateLimiterOptions.OnRejected"/> and sets <see cref="RateLimiterOptions.RejectionStatusCode"/>.
    /// </remarks>
    public static RateLimiterOptions SetApiRejectionResponse(this RateLimiterOptions options, string? message = null)
    {
        message ??= DefaultRejectionMessage;
        return options.SetApiRejectionResponse(rejection => rejection.RejectionMessage ?? message);
    }

    /// <summary>
    /// Configures rejected requests to receive a <see cref="TooManyRequestsApiException"/> response in the toolkit's error format, as
    /// <see cref="SetApiRejectionResponse(RateLimiterOptions, string)"/> does, with the message that a function returns for each rejected request.
    /// </summary>
    /// <param name="options">The rate limiter options.</param>
    /// <param name="getMessage">A function that returns the message reported to the client, given the details of the rejected request: its context, the
    /// <see cref="ClientRateLimitOptions.RejectionMessage"/> of the per-client limit that rejected it, and how long the client should wait.</param>
    /// <remarks>
    /// <inheritdoc cref="SetApiRejectionResponse(RateLimiterOptions, string)" path="/remarks"/>
    /// </remarks>
    public static RateLimiterOptions SetApiRejectionResponse(this RateLimiterOptions options, Func<RateLimitRejectionContext, string> getMessage)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, cancellationToken) => {
            if (context.Lease.TryGetMetadata(ClientFixedWindowRateLimiter.ErrorMetadata, out var error) && error is not null)
                return new ValueTask(context.HttpContext.Response.WriteApiErrorAsync(error, cancellationToken));

            context.Lease.TryGetMetadata(ClientFixedWindowRateLimiter.RejectionMessageMetadata, out string? limitMessage);
            TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var time) ? time : null;

            var exception = new TooManyRequestsApiException(getMessage(new RateLimitRejectionContext(context.HttpContext, limitMessage, retryAfter))) {
                ErrorCode = ApiErrorCodes.RateLimited,
                RetryAfter = retryAfter,
            };

            return new ValueTask(context.HttpContext.Response.WriteApiErrorAsync(exception, cancellationToken));
        };

        return options;
    }

    // Without a rejection handler, ASP.NET Core rejects requests with an empty 503 response.
    private static void SetDefaultRejectionResponse(RateLimiterOptions options)
    {
        if (options.OnRejected is null)
            options.SetApiRejectionResponse();
    }
}
