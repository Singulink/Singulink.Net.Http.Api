using Microsoft.AspNetCore.SignalR;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for configuring <see cref="HubOptions"/>.
/// </summary>
public static class ApiHubOptionsExtensions
{
    /// <summary>
    /// Adds the <see cref="ApiExceptionHubFilter"/> so that hub exceptions are reported to clients as API errors.
    /// </summary>
    public static HubOptions AddApiExceptionFilter(this HubOptions options)
    {
        options.AddFilter<ApiExceptionHubFilter>();
        return options;
    }
}
