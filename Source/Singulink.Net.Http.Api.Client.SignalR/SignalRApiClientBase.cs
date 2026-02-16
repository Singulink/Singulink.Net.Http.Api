using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Base class for API clients that support SignalR hub connections.
/// </summary>
public abstract class SignalRApiClientBase : ApiClientBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class with an optional HTTP client factory.
    /// </summary>
    protected SignalRApiClientBase(IHttpClientFactory? httpClientFactory = null) : base(httpClientFactory)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class with an optional HTTP client factory, session token and session token
    /// changed callback.
    /// </summary>
    protected SignalRApiClientBase(IHttpClientFactory? httpClientFactory, string? sessionToken, Action<string?>? sessionTokenChanged)
        : base(httpClientFactory, sessionToken, sessionTokenChanged)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class that shares the session state with the specified parent client.
    /// </summary>
    protected SignalRApiClientBase(ApiClientBase parent) : base(parent)
    {
    }

    /// <summary>
    /// Creates a new <see cref="HubConnectionBuilder"/> for building hub connections. Default implementation returns a builder with automatic reconnect
    /// enabled. Override to customize the builder (e.g. add <c>MessagePackHubProtocol</c>, adjust reconnect policy, etc.).
    /// </summary>
    protected virtual HubConnectionBuilder CreateHubConnectionBuilder()
    {
        var builder = new HubConnectionBuilder();
        builder.WithAutomaticReconnect();
        return builder;
    }

    /// <summary>
    /// Creates a new <see cref="HubConnection"/> for the specified hub path with optional query string parameters. Default query
    /// parameters from <see cref="ApiClientBase.GetDefaultQueryParams(string)"/> are automatically merged with per-call parameters (per-call parameters with the
    /// same name take precedence). Session cookies and user agent are applied automatically on non-browser platforms.
    /// </summary>
    /// <param name="path">The hub endpoint path relative to the base address.</param>
    /// <param name="queryStringParams">Optional query string parameters to include in the hub connection URL.</param>
    protected HubConnection CreateHubConnection(string path, params ReadOnlySpan<(string Name, object? Value)> queryStringParams)
    {
        return CreateHubConnection(path, null, queryStringParams);
    }

    /// <summary>
    /// Creates a new <see cref="HubConnection"/> for the specified hub path with optional connection options and query string parameters. Default query
    /// parameters from <see cref="ApiClientBase.GetDefaultQueryParams(string)"/> are automatically merged with per-call parameters (per-call parameters with the
    /// same name take precedence). Session cookies and user agent are applied automatically on non-browser platforms.
    /// </summary>
    /// <param name="path">The hub endpoint path relative to the base address.</param>
    /// <param name="configureOptions">Callback that configures the <see cref="HttpConnectionOptions"/> for the connection.</param>
    /// <param name="queryStringParams">Optional query string parameters to include in the hub connection URL.</param>
    protected virtual HubConnection CreateHubConnection(
        string path,
        Action<HttpConnectionOptions>? configureOptions,
        params ReadOnlySpan<(string Name, object? Value)> queryStringParams)
    {
        var hubUri = GetApiUrl(path, queryStringParams);
        var builder = CreateHubConnectionBuilder();

        builder.WithUrl(hubUri, options =>
        {
            if (OperatingSystem.IsBrowser())
            {
                options.Transports = HttpTransportType.WebSockets;
            }
            else
            {
                string? sessionToken = SessionToken;

                if (sessionToken is not null)
                    options.Headers["Cookie"] = $"{SessionCookieName}={sessionToken}";

                options.Headers["User-Agent"] = UserAgent;
            }

            configureOptions?.Invoke(options);
        });

        return builder.Build();
    }
}
