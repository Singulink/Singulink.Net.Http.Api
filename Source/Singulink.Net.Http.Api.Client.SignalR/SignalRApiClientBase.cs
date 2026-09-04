using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Base class for API clients that support SignalR hub connections.
/// </summary>
public abstract class SignalRApiClientBase : ApiClientBase
{
    private const string SerializationUnreferencedCodeMessage =
        "JSON serialization and deserialization might require types that cannot be statically analyzed. Use a constructor that accepts a source-generated " +
        "JsonSerializerContext (IJsonTypeInfoResolver) to ensure compatibility with trimming.";

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class using reflection-based JSON serialization with default <see
    /// cref="JsonSerializerDefaults.Web"/> options.
    /// </summary>
    /// <remarks>
    /// This constructor uses reflection-based JSON serialization which is not compatible with trimming. To support trimming, use a constructor that
    /// accepts a source-generated <see cref="IJsonTypeInfoResolver"/> (e.g. a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>).
    /// </remarks>
    [RequiresUnreferencedCode(SerializationUnreferencedCodeMessage)]
    protected SignalRApiClientBase()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class using reflection-based JSON serialization with default <see
    /// cref="JsonSerializerDefaults.Web"/> options, the specified session token and session token changed callback.
    /// </summary>
    /// <remarks>
    /// This constructor uses reflection-based JSON serialization which is not compatible with trimming. To support trimming, use a constructor that
    /// accepts a source-generated <see cref="IJsonTypeInfoResolver"/> (e.g. a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>).
    /// </remarks>
    [RequiresUnreferencedCode(SerializationUnreferencedCodeMessage)]
    protected SignalRApiClientBase(string? sessionToken, Action<string?>? sessionTokenChanged)
        : base(sessionToken, sessionTokenChanged)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class using the specified JSON serializer options, with an optional session
    /// token and session token changed callback.
    /// </summary>
    /// <param name="serializerOptions">The JSON serializer options used for serializing and deserializing API request and response content.</param>
    /// <param name="sessionToken">The initial session token.</param>
    /// <param name="sessionTokenChanged">A callback invoked when the session token changes.</param>
    /// <remarks>
    /// This constructor accepts arbitrary serializer options whose trimming compatibility cannot be statically verified, so it is not compatible with
    /// trimming. To support trimming, use a constructor that accepts a source-generated <see cref="IJsonTypeInfoResolver"/> (e.g. a <see
    /// cref="System.Text.Json.Serialization.JsonSerializerContext"/>).
    /// </remarks>
    [RequiresUnreferencedCode(SerializationUnreferencedCodeMessage)]
    protected SignalRApiClientBase(JsonSerializerOptions serializerOptions, string? sessionToken = null, Action<string?>? sessionTokenChanged = null)
        : base(serializerOptions, sessionToken, sessionTokenChanged)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class using JSON serialization backed by the specified source-generated type
    /// information resolver (e.g. a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>). This constructor is compatible with trimming.
    /// </summary>
    /// <param name="serializerContext">The source-generated JSON type information resolver used for serialization and deserialization.</param>
    /// <param name="configureOptions">An optional callback to further configure the JSON serializer options created from the resolver.</param>
    protected SignalRApiClientBase(IJsonTypeInfoResolver serializerContext, Action<JsonSerializerOptions>? configureOptions = null)
        : base(serializerContext, configureOptions)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class using JSON serialization backed by the specified source-generated type
    /// information resolver (e.g. a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>), with the specified session token and session token
    /// changed callback. This constructor is compatible with trimming.
    /// </summary>
    /// <param name="serializerContext">The source-generated JSON type information resolver used for serialization and deserialization.</param>
    /// <param name="sessionToken">The initial session token.</param>
    /// <param name="sessionTokenChanged">A callback invoked when the session token changes.</param>
    /// <param name="configureOptions">An optional callback to further configure the JSON serializer options created from the resolver.</param>
    protected SignalRApiClientBase(
        IJsonTypeInfoResolver serializerContext,
        string? sessionToken,
        Action<string?>? sessionTokenChanged,
        Action<JsonSerializerOptions>? configureOptions = null)
        : base(serializerContext, sessionToken, sessionTokenChanged, configureOptions)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class that shares the session state and JSON serializer options with the
    /// specified parent client.
    /// </summary>
    protected SignalRApiClientBase(ApiClientBase parent) : base(parent)
    {
    }

    /// <summary>
    /// Creates a new <see cref="HubConnectionBuilder"/> for building hub connections. Default implementation returns a builder with automatic reconnect
    /// enabled. Override to customize the builder (e.g. adjust reconnect policy, etc.).
    /// </summary>
    protected virtual IHubConnectionBuilder CreateHubConnectionBuilder() => new HubConnectionBuilder()
            .AddJsonProtocol(options => options.PayloadSerializerOptions = SerializerOptions)
            .WithAutomaticReconnect();

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
        var builder = CreateHubConnectionBuilder().WithUrl(hubUri, options =>
        {
            options.Transports = HttpTransportType.WebSockets;

            if (!OperatingSystem.IsBrowser())
            {
                // The session token can change at any time (i.e. it gets refreshed by API responses), so it is read live for every negotiate request and
                // websocket handshake instead of being baked into the connection options. This is essential for automatic reconnects, which would otherwise
                // present a stale token that fails validation on the server and invalidates the session.

                options.HttpMessageHandlerFactory = _ => new SessionMessageHandler(this);
                options.WebSocketConfiguration = wsOptions => {
                    wsOptions.SetRequestHeader("User-Agent", UserAgent);

                    if (SessionToken is { } sessionToken)
                        wsOptions.SetRequestHeader("Cookie", $"{SessionCookieName}={sessionToken}");
                };
            }

            configureOptions?.Invoke(options);
        });

        return builder.Build();
    }

    /// <summary>
    /// Applies the current session cookie and user agent to each outgoing hub HTTP request (negotiate and, on runtimes that route the websocket handshake
    /// through the HTTP handler, the handshake) and captures session token updates from responses.
    /// </summary>
    private sealed class SessionMessageHandler : DelegatingHandler
    {
        private readonly SignalRApiClientBase _client;

        public SessionMessageHandler(SignalRApiClientBase client) : base(HttpMessageHandler!)
        {
            _client = client;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", _client.UserAgent);
            request.Headers.Remove("Cookie");

            if (_client.SessionToken is { } sessionToken)
                request.Headers.TryAddWithoutValidation("Cookie", $"{_client.SessionCookieName}={sessionToken}");

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _client.UpdateSessionToken(response);
            return response;
        }
    }
}
