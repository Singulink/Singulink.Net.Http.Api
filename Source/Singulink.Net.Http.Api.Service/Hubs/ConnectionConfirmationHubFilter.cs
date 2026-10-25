using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Hub filter that sends a connection the confirmation message (<see cref="ToolkitHubMessages.Connected"/>) once the hub's <c>OnConnectedAsync</c>, and
/// the filters that run inside this one, succeed. The toolkit's client counts a connection as connected only once it receives the message, since SignalR
/// completes the handshake before the hub runs its <c>OnConnectedAsync</c>, which can still refuse the connection. Other clients ignore the message.
/// </summary>
internal sealed class ConnectionConfirmationHubFilter : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await next(context);
        await context.Hub.Clients.Caller.SendAsync(ToolkitHubMessages.Connected);
    }

    /// <summary>
    /// Adds the filter to the hub options after the filters that the app adds to them, so that it runs inside those filters, around the hub-specific filters
    /// and the hub, whatever the order in which the app registers its services.
    /// </summary>
    internal sealed class Setup : IPostConfigureOptions<HubOptions>
    {
        public void PostConfigure(string? name, HubOptions options) => options.AddFilter(new ConnectionConfirmationHubFilter());
    }
}
