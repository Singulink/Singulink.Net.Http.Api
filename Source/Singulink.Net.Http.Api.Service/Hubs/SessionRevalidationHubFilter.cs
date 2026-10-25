using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Hub filter that validates the session of a connection when it connects if its cookie is due, and keeps track of the connections that have a session,
/// so that their sessions are rechecked while they are open (see <see cref="SessionHandlingOptions.RevalidateHubConnections"/>). The hub method calls of a
/// connection whose session ended fail with the error that ended it until the connection closes.
/// </summary>
internal sealed class SessionRevalidationHubFilter(IReadOnlyList<ISessionConnectionTracker> trackers) : IHubFilter
{
    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        foreach (var tracker in trackers)
        {
            if (tracker.GetSessionEndedError(invocationContext.Context) is { } error)
                throw new HubException(error);
        }

        return next(invocationContext);
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        foreach (var tracker in trackers)
        {
            try
            {
                await tracker.ValidateIfDueAsync(context.Context);
            }
            catch (Exception ex) when (!context.Context.ConnectionAborted.IsCancellationRequested)
            {
                // Reported as the API exception filter reports it, also when this filter runs before that one.
                var exception = await ApiExceptionHubFilter.GetExceptionToThrowAsync(context.Context, ex);

                if (exception == ex)
                    throw;

                ExceptionDispatchInfo.Throw(exception);
                throw;
            }
        }

        await next(context);

        // The hub's own session check has run by now, so the tracked session is the one it validated, if it did.
        foreach (var tracker in trackers)
            tracker.Track(context.Context, context.Hub.GetType());
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        foreach (var tracker in trackers)
            tracker.Untrack(context.Context);

        return next(context, exception);
    }

    /// <summary>
    /// Adds the filter to the hub options of an app that registers session handling, unless
    /// <see cref="SessionHandlingOptions.RevalidateHubConnections"/> is <see langword="false"/>.
    /// </summary>
    internal sealed class Setup(IOptions<SessionHandlingOptions> sessionOptions, IEnumerable<ISessionConnectionTracker> trackers)
        : IConfigureOptions<HubOptions>
    {
        public void Configure(HubOptions options)
        {
            if (sessionOptions.Value.RevalidateHubConnections)
                options.AddFilter(new SessionRevalidationHubFilter([.. trackers]));
        }
    }
}
