using Microsoft.AspNetCore.RateLimiting;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Provides the details of a request that rate limiting rejected to the function that returns the message of its error (see
/// <see cref="RateLimiterOptionsExtensions.SetApiRejectionResponse(RateLimiterOptions, Func{RateLimitRejectionContext, string})"/>).
/// </summary>
public sealed class RateLimitRejectionContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitRejectionContext"/> class.
    /// </summary>
    /// <param name="httpContext">The context of the rejected request.</param>
    /// <param name="rejectionMessage">The rejection message of the per-client limit that rejected the request, if it has one.</param>
    /// <param name="retryAfter">How long the client should wait before it tries again, if the limiter that rejected the request reports it.</param>
    public RateLimitRejectionContext(HttpContext httpContext, string? rejectionMessage, TimeSpan? retryAfter)
    {
        HttpContext = httpContext;
        RejectionMessage = rejectionMessage;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the context of the rejected request.
    /// </summary>
    public HttpContext HttpContext { get; }

    /// <summary>
    /// Gets the <see cref="ClientRateLimitOptions.RejectionMessage"/> of the per-client limit that rejected the request, or <see langword="null"/> if the
    /// limit has none or another limiter rejected the request.
    /// </summary>
    public string? RejectionMessage { get; }

    /// <summary>
    /// Gets how long the client should wait before it tries again, or <see langword="null"/> if the limiter that rejected the request doesn't report it.
    /// For the per-client limits, it is the time until their next window starts. The error carries it as <see cref="ApiException.RetryAfter"/>, and the
    /// response's <c>Retry-After</c> header in whole seconds, rounded up.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
