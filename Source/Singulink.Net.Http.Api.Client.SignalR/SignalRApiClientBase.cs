using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.Client.InternalServices;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Base class for API clients that support SignalR hub connections.
/// </summary>
public abstract class SignalRApiClientBase : ApiClientBase
{
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
    /// <param name="sessionToken">The token of a persistent session to continue, such as one stored by <paramref name="sessionTokenChanged"/>.</param>
    /// <param name="sessionTokenChanged">A callback that stores the token of a persistent session. It is called with the token when a persistent session
    /// starts or its token changes, and with <see langword="null"/> when the stored token should be deleted. An exception that it throws propagates to
    /// the request or call that changed the token.</param>
    /// <remarks>
    /// <para>
    /// The options are used as they are, without the API null handling that the constructors that take a type info resolver apply (see <see
    /// cref="ApiNullHandlingExtensions.ConfigureApiNullHandling(JsonSerializerOptions)"/>).</para>
    /// <para>
    /// This constructor accepts arbitrary serializer options whose trimming compatibility cannot be statically verified, so it is not compatible with
    /// trimming. To support trimming, use a constructor that accepts a source-generated <see cref="IJsonTypeInfoResolver"/> (e.g. a <see
    /// cref="System.Text.Json.Serialization.JsonSerializerContext"/>).</para>
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
    /// <param name="typeInfoResolver">The source-generated JSON type information resolver used for serialization and deserialization.</param>
    /// <param name="configureOptions">An optional callback that further configures the JSON serializer options created from the resolver.</param>
    /// <inheritdoc cref="ApiClientBase(IJsonTypeInfoResolver, string?, Action{string?}?, Action{JsonSerializerOptions}?)" path="/remarks"/>
    protected SignalRApiClientBase(IJsonTypeInfoResolver typeInfoResolver, Action<JsonSerializerOptions>? configureOptions = null)
        : base(typeInfoResolver, configureOptions)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class using JSON serialization backed by the specified source-generated type
    /// information resolver (e.g. a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>), with the specified session token and session token
    /// changed callback. This constructor is compatible with trimming.
    /// </summary>
    /// <param name="typeInfoResolver">The source-generated JSON type information resolver used for serialization and deserialization.</param>
    /// <param name="sessionToken">The token of a persistent session to continue, such as one stored by <paramref name="sessionTokenChanged"/>.</param>
    /// <param name="sessionTokenChanged">A callback that stores the token of a persistent session. It is called with the token when a persistent session
    /// starts or its token changes, and with <see langword="null"/> when the stored token should be deleted. An exception that it throws propagates to
    /// the request or call that changed the token.</param>
    /// <param name="configureOptions">An optional callback that further configures the JSON serializer options created from the resolver.</param>
    /// <inheritdoc cref="ApiClientBase(IJsonTypeInfoResolver, string?, Action{string?}?, Action{JsonSerializerOptions}?)" path="/remarks"/>
    protected SignalRApiClientBase(
        IJsonTypeInfoResolver typeInfoResolver,
        string? sessionToken,
        Action<string?>? sessionTokenChanged,
        Action<JsonSerializerOptions>? configureOptions = null)
        : base(typeInfoResolver, sessionToken, sessionTokenChanged, configureOptions)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRApiClientBase"/> class that shares the session state and JSON serializer options with the
    /// specified parent client, and by default its base address, request timeout, user agent, session cookie name, user ID precondition query name, user ID
    /// precondition and HTTP handler.
    /// </summary>
    /// <inheritdoc cref="ApiClientBase(ApiClientBase, InterpolatedApiPathHandler, object?)"/>
    protected SignalRApiClientBase(ApiClientBase parent, InterpolatedApiPathHandler basePath = default, object? userIdPrecondition = null)
        : base(parent, basePath, userIdPrecondition)
    {
    }

    /// <summary>
    /// Gets or sets the time provider that times the attempts of the hub connections that the client creates to start again (see
    /// <see cref="ApiHubConnection.StayConnected"/>), for tests. Default is <see cref="TimeProvider.System"/>.
    /// </summary>
    internal TimeProvider HubConnectionTimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets a value indicating whether the hub connections that the client creates take the browser's path for the session-ended reports: their
    /// access token provider records their handshakes, their reports are judged by when they last connected rather than by their token (see
    /// <see cref="ApiClientBase.EndSession(bool, string?, Task)"/>), and they try the browser's session cookie after the end of their session, at once after
    /// a successful request with their user ID precondition (see <see cref="ApiHubConnection.StayConnected"/>). It is <see langword="true"/> on browser
    /// platforms, and tests set it on others.
    /// </summary>
    internal bool UsesBrowserSessionPath { get; set; } = OperatingSystem.IsBrowser();

    /// <summary>
    /// Creates the builder that hub connections are built with. By default, the builder uses <see cref="ApiClientBase.SerializerOptions"/> for hub
    /// payloads and has automatic reconnect enabled with SignalR's default policy.
    /// </summary>
    protected virtual IHubConnectionBuilder CreateHubConnectionBuilder() => new HubConnectionBuilder()
            .AddJsonProtocol(options => options.PayloadSerializerOptions = SerializerOptions)
            .WithAutomaticReconnect();

    /// <summary>
    /// Creates a new <see cref="ApiHubConnection"/> for the specified hub path and query string parameters. The URL includes the user ID precondition like
    /// request URLs do (see <see cref="ApiClientBase.GetApiUrl(InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}})"/>). Session cookies
    /// and user agent are applied automatically on non-browser platforms.
    /// </summary>
    /// <param name="path">The hub endpoint path relative to the base address.</param>
    /// <param name="query">The query string parameters of the hub connection URL, such as <c>[("documentId", id)]</c>. Parameters with a
    /// <see langword="null"/> value are left out.</param>
    protected ApiHubConnection CreateHubConnection(InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default)
    {
        return CreateHubConnection(path, null, query);
    }

    /// <summary>
    /// Creates a new <see cref="ApiHubConnection"/> for the specified hub path, connection options and query string parameters. The URL includes the user
    /// ID precondition like request URLs do (see <see cref="ApiClientBase.GetApiUrl(InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}})"/>).
    /// Session cookies and user agent are applied automatically on non-browser platforms.
    /// </summary>
    /// <param name="path">The hub endpoint path relative to the base address.</param>
    /// <param name="configureOptions">Callback that configures the <see cref="HttpConnectionOptions"/> for the connection. The connection uses the WebSockets
    /// transport without a negotiate request (<see cref="HttpConnectionOptions.SkipNegotiation"/>) when the callback runs.</param>
    /// <param name="query">The query string parameters of the hub connection URL, such as <c>[("documentId", id)]</c>. Parameters with a
    /// <see langword="null"/> value are left out.</param>
    /// <remarks>
    /// <para>
    /// Without a negotiate request, the connection has no connection ID (<see cref="ApiHubConnection.ConnectionId"/> is <see langword="null"/>), and stateful
    /// reconnect is off.</para>
    /// <para>
    /// On non-browser platforms, the WebSocket handshake of a connection without a negotiate request does not follow redirects: a redirect fails the
    /// connection. This does not apply if <paramref name="configureOptions"/> sets <see cref="HttpConnectionOptions.WebSocketFactory"/>, which then connects
    /// the WebSocket instead.</para>
    /// </remarks>
    protected virtual ApiHubConnection CreateHubConnection(
        InterpolatedApiPathHandler path,
        Action<HttpConnectionOptions>? configureOptions,
        ReadOnlySpan<(string Name, object? Value)> query = default)
    {
        var hubUri = GetApiUrl(path, query);

        // The session token that the connection was last established with, and the connection, whose handshakes record their session start (see
        // ApiHubConnection.RecordHandshake), which the wait for a new session and the browser's judgment of the connection's reports use.
        string? connectionSessionToken = null;
        ApiHubConnection? hubConnection = null;
        bool usesBrowserPath = UsesBrowserSessionPath;

        // Whether the client connects the WebSocket itself, without following redirects.
        bool connectsWebSocket = false;

        var builder = CreateHubConnectionBuilder().WithUrl(hubUri, options =>
        {
            // The WebSockets transport needs no negotiate request. A negotiate response can send the connection, and with it the session cookie, to another
            // address.
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;

            if (!OperatingSystem.IsBrowser())
            {
                // The session token can change at any time (e.g. it gets refreshed by API responses), so it is read for every WebSocket handshake, and
                // every negotiate request if the connection options turn negotiation back on, instead of being baked into the connection options. This is
                // essential for automatic reconnects, which would otherwise present a stale token that fails validation on the server and invalidates the
                // session.

                options.HttpMessageHandlerFactory = _ => new SessionMessageHandler(this);
                options.WebSocketConfiguration = wsOptions => {
                    // The access token provider records the handshake on the browser's path.
                    if (!usesBrowserPath)
                        hubConnection?.RecordHandshake();

                    wsOptions.SetRequestHeader("User-Agent", UserAgent);

                    if (connectsWebSocket)
                        return;

                    connectionSessionToken = SessionToken;

                    // SignalR's handshake follows redirects and sends a cookie header to wherever they lead, so the session cookie goes into a cookie
                    // container, which only sends it to the hub's host. The cookies of the connection options are kept.
                    var cookies = new CookieContainer();

                    if (wsOptions.Cookies is { Count: > 0 } optionsCookies)
                        cookies.Add(optionsCookies.GetAllCookies());

                    if (connectionSessionToken is not null)
                        cookies.Add(hubUri, new Cookie(SessionCookieName, connectionSessionToken, "/"));

                    wsOptions.Cookies = cookies;
                };
            }

            configureOptions?.Invoke(options);

            if (usesBrowserPath)
            {
                // The browser sends its cookie of the moment with each handshake, and its WebSocket can't be configured (WebSocketConfiguration is
                // unsupported in the browser). SignalR documents that it calls the access token provider before every HTTP request it makes, which by
                // default are the WebSocket handshakes, also those of its automatic reconnects, so the handshake's session start is recorded there, once the
                // app's provider, if any, gave its token. The connection ignores the calls once it connected, such as for the polls of long polling or a
                // refresh of the authentication of an open connection, and keeps the session start of the handshake that connected it.
                var accessTokenProvider = options.AccessTokenProvider;

                options.AccessTokenProvider = async () => {
                    string? token = accessTokenProvider is null ? null : await accessTokenProvider().ConfigureAwait(false);
                    hubConnection?.RecordHandshake();

                    return token;
                };
            }

            if (!OperatingSystem.IsBrowser() && options.SkipNegotiation && options.WebSocketFactory is null)
            {
                // Without a negotiate request, the WebSocket connects to the hub's address, and the client connects it itself unless the app does.
                connectsWebSocket = true;
                options.WebSocketFactory = (context, cancellationToken) =>
                    ConnectWebSocketAsync(context, sessionToken => connectionSessionToken = sessionToken, cancellationToken);
            }
        });

        // The toolkit's own messages don't depend on the app's type info for payloads.
        builder.Services.PostConfigure<JsonHubProtocolOptions>(options =>
            options.PayloadSerializerOptions = ToolkitHubJsonContext.AddTo(options.PayloadSerializerOptions));

        // A hub error that says the connection's session has ended, and the service's report that it ended, end it on this device too, as a cleared
        // session cookie does for requests, and a connection that stays connected waits for the next session to start again. In the browser, whose tabs
        // share the session cookie, it also tries the browser's cookie, which another tab's sign-in can have replaced, and sooner when a request with its
        // user ID precondition, or with any for a connection without one, succeeds. Its errors for another user's session carry its precondition.
        string? userIdPrecondition = GetUserIdPrecondition(hubUri);

        hubConnection = new ApiHubConnection(
            builder.Build(),
            (exception, sessionStart) => OnApiError(usesBrowserPath, exception, connectionSessionToken, sessionStart),
            () => NextSessionStart,
            HubConnectionTimeProvider,
            sessionStart => EndSession(usesBrowserPath, connectionSessionToken, sessionStart),
            usesBrowserPath,
            cancellationToken => WaitForRequestCueAsync(userIdPrecondition, cancellationToken),
            userIdPrecondition);

        return hubConnection;
    }

    /// <summary>
    /// Connects the WebSocket of a hub connection with the connection options applied, through a handler that does not follow redirects. A handshake that
    /// follows a redirect sends its headers, and so the session cookie, to wherever it leads.
    /// </summary>
    /// <param name="context">The context that SignalR passes to the WebSocket factory.</param>
    /// <param name="sessionTokenSent">Receives the session token that the handshake sends, if any.</param>
    /// <param name="cancellationToken">A token to cancel the handshake.</param>
    private async ValueTask<WebSocket> ConnectWebSocketAsync(
        WebSocketConnectionContext context, Action<string?> sessionTokenSent, CancellationToken cancellationToken)
    {
        var options = context.Options;
        var webSocket = new ClientWebSocket();
        var wsOptions = webSocket.Options;
        var defaultProxy = wsOptions.Proxy;
        HttpMessageInvoker? invoker = null;

        try
        {
            wsOptions.SetRequestHeader("User-Agent", UserAgent);
            wsOptions.SetRequestHeader("X-Requested-With", "XMLHttpRequest");

            foreach (var header in options.Headers)
                wsOptions.SetRequestHeader(header.Key, header.Value);

            wsOptions.Cookies = options.Cookies;
            wsOptions.Credentials = options.Credentials;

            if (options.ClientCertificates is { Count: > 0 } clientCertificates)
                wsOptions.ClientCertificates.AddRange(clientCertificates);

            if (options.Proxy is not null)
                wsOptions.Proxy = options.Proxy;

            if (options.UseDefaultCredentials is { } useDefaultCredentials)
                wsOptions.UseDefaultCredentials = useDefaultCredentials;

            options.WebSocketConfiguration?.Invoke(wsOptions);

            if (options.AccessTokenProvider is not null && await options.AccessTokenProvider().ConfigureAwait(false) is { } accessToken &&
                !string.IsNullOrWhiteSpace(accessToken))
            {
                wsOptions.SetRequestHeader("Authorization", "Bearer " + accessToken);
            }

            // A handshake with a handler of the caller's rejects the options that configure the connection, so they move to the handler.
            var handler = new SocketsHttpHandler {
                AllowAutoRedirect = false,
                UseCookies = false,
                Credentials = wsOptions.UseDefaultCredentials ? CredentialCache.DefaultCredentials : wsOptions.Credentials,
            };

            invoker = new HttpMessageInvoker(handler);

            if (wsOptions.Proxy is null)
                handler.UseProxy = false;
            else if (wsOptions.Proxy != defaultProxy)
                handler.Proxy = wsOptions.Proxy;

            handler.SslOptions.RemoteCertificateValidationCallback = wsOptions.RemoteCertificateValidationCallback;

            if (wsOptions.ClientCertificates.Count > 0)
                handler.SslOptions.ClientCertificates = new X509CertificateCollection(wsOptions.ClientCertificates);

            // The cookies are sent as a header, which only goes to the hub's address since redirects are not followed.
            string? sessionToken = IsBaseAddressHost(context.Uri) ? SessionToken : null;
            string? optionsCookies = wsOptions.Cookies?.GetCookieHeader(ToHttpUri(context.Uri));
            sessionTokenSent(sessionToken);

            string? cookieHeader = (sessionToken, optionsCookies) switch {
                (null, null or "") => null,
                (null, _) => optionsCookies,
                (_, null or "") => $"{SessionCookieName}={sessionToken}",
                _ => $"{SessionCookieName}={sessionToken}; {optionsCookies}",
            };

            if (cookieHeader is not null)
                wsOptions.SetRequestHeader("Cookie", cookieHeader);

            wsOptions.Cookies = null;
            wsOptions.Credentials = null;
            wsOptions.UseDefaultCredentials = false;
            wsOptions.Proxy = defaultProxy;
            wsOptions.ClientCertificates.Clear();
            wsOptions.RemoteCertificateValidationCallback = null;

            await webSocket.ConnectAsync(context.Uri, invoker, cancellationToken).ConfigureAwait(false);
            return new InvokerOwningWebSocket(webSocket, invoker);
        }
        catch
        {
            webSocket.Dispose();
            invoker?.Dispose();
            throw;
        }

        static Uri ToHttpUri(Uri uri)
        {
            string scheme = string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            return new UriBuilder(uri) { Scheme = scheme }.Uri;
        }
    }

    /// <summary>
    /// Applies the current session cookie and user agent to the HTTP requests of a hub connection that go through its HTTP handler, such as a negotiate
    /// request when the connection options turn negotiation back on, and captures session token updates from responses. The session cookie is only sent to
    /// the host of the base address. Redirects are not followed.
    /// </summary>
    private sealed class SessionMessageHandler : DelegatingHandler
    {
        private readonly SignalRApiClientBase _client;

        public SessionMessageHandler(SignalRApiClientBase client) : base(SharedHttpHandler!)
        {
            _client = client;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", _client.UserAgent);
            request.Headers.Remove("Cookie");

            // A negotiate response can send the connection to another address, such as Azure SignalR Service, which the session cookie is not sent to.
            string? sentToken = _client.IsBaseAddressHost(request.RequestUri) ? _client.SessionToken : null;

            if (sentToken is not null)
                request.Headers.TryAddWithoutValidation("Cookie", $"{_client.SessionCookieName}={sentToken}");

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _client.UpdateSessionToken(request, response, sentToken, _client.IsSessionChange(request, response));
            return response;
        }
    }

    /// <summary>
    /// A WebSocket that disposes the invoker that its handshake was sent with when it is disposed.
    /// </summary>
    private sealed class InvokerOwningWebSocket(WebSocket webSocket, HttpMessageInvoker invoker) : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => webSocket.CloseStatus;

        public override string? CloseStatusDescription => webSocket.CloseStatusDescription;

        public override WebSocketState State => webSocket.State;

        public override string? SubProtocol => webSocket.SubProtocol;

        public override void Abort() => webSocket.Abort();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            webSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            webSocket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            webSocket.ReceiveAsync(buffer, cancellationToken);

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            webSocket.ReceiveAsync(buffer, cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            webSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            webSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken) =>
            webSocket.SendAsync(buffer, messageType, messageFlags, cancellationToken);

        public override void Dispose()
        {
            webSocket.Dispose();
            invoker.Dispose();
        }
    }
}
