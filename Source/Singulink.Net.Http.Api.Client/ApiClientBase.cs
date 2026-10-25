using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Singulink.Net.Http.Api.Client.InternalServices;
using Singulink.Net.Http.Api.Client.Utilities;

#if NET
using Microsoft.AspNetCore.Components.WebAssembly.Http;
#endif

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Base class for API clients.
/// </summary>
public abstract partial class ApiClientBase
{
    internal const string SerializationUnreferencedCodeMessage =
        "JSON serialization and deserialization might require types that cannot be statically analyzed. Use a constructor that accepts a source-generated " +
        "JsonSerializerContext (IJsonTypeInfoResolver) to ensure compatibility with trimming.";

    private const string DefaultUserIdPreconditionQueryName = "if-userId";
    private const string DefaultSessionCookieName = "__Host-session-token";
    private const int DefaultHttpClientRefreshDnsTimeout = 120 * 1000;
    private const string RequestTimeoutOptionName = "Singulink.Net.Http.Api.Client.RequestTimeout";

    private const string SerializationSuppressionJustification =
        "The trimming requirement is enforced on the reflection-based constructors via RequiresUnreferencedCode, which also warns in AOT builds. The " +
        "trim-safe constructors supply a source-generated IJsonTypeInfoResolver, so no unreferenced or dynamic code is used at runtime.";

    internal static readonly HttpMessageHandler? SharedHttpHandler = OperatingSystem.IsBrowser() ? null : new SharedApiHttpHandler();

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(100);

    private static readonly HttpClient _httpClient = CreateHttpClient();

#if NET
    private static readonly HttpMethod PatchMethod = HttpMethod.Patch;
    private static readonly HttpRequestOptionsKey<TimeSpan> RequestTimeoutOptionKey = new(RequestTimeoutOptionName);
#else
    private static readonly HttpMethod PatchMethod = new("PATCH");
#endif

    private readonly ApiClientBase? _parentClient;
    private readonly SessionState _sessionState;
    private readonly string? _basePath;
    private readonly string? _userIdPrecondition;
    private readonly HttpClient? _handlerHttpClient;

    /// <summary>
    /// Gets the JSON serializer options used for serializing and deserializing API request and response content. The constructors that take a type info
    /// resolver create options with <see cref="JsonSerializerDefaults.Web"/> defaults and the API null handling (see <see
    /// cref="ApiNullHandlingExtensions.ConfigureApiNullHandling(JsonSerializerOptions)"/>), the reflection-based constructors without options use <see
    /// cref="JsonSerializerDefaults.Web"/> defaults, and the constructor that takes options uses them as they are.
    /// </summary>
    protected JsonSerializerOptions SerializerOptions { get; }

    /// <summary>
    /// Gets the name of the cookie that holds the session token, which must match the service's <c>SessionHandlingOptions.SessionCookieName</c>. If a
    /// parent client exists, returns the parent's value by default; otherwise <c>"__Host-session-token"</c>.
    /// </summary>
    protected virtual string SessionCookieName => _parentClient?.SessionCookieName ?? DefaultSessionCookieName;

    /// <summary>
    /// Gets the name of the query string parameter that carries the user ID precondition, which must match the service's
    /// <c>SessionHandlingOptions.UserIdPreconditionQueryName</c>. If a parent client exists, returns the parent's value by default; otherwise
    /// <c>"if-userId"</c>.
    /// </summary>
    protected virtual string UserIdPreconditionQueryName => _parentClient?.UserIdPreconditionQueryName ?? DefaultUserIdPreconditionQueryName;

    /// <summary>
    /// Gets the user agent string sent with requests and with the handshakes of hub connections on non-browser platforms, where browsers send their own.
    /// If a parent client exists, returns the parent's value by default; otherwise the name and version of the app's entry assembly followed by the
    /// operating system, such as <c>MyApp/1.2.0.0 (Microsoft Windows 10.0.26100)</c>, or the name and version of this library if the app has no entry
    /// assembly, as .NET for Android apps don't, or its name can't be used in a user agent. <see cref="CreateUserAgent(string, string)"/> creates a user
    /// agent of the same form with another product name and version.
    /// </summary>
    protected virtual string UserAgent
    {
        get {
            if (_parentClient is not null)
                return _parentClient.UserAgent;

            return field ??= CreateDefaultUserAgent(Assembly.GetEntryAssembly()?.GetName(), RuntimeInformation.OSDescription);
        }
    }

    /// <summary>
    /// Gets how long a request can take before it is cancelled: from sending it until the response is read, or until the response starts for a streaming
    /// response or a request that returns the <see cref="HttpResponseMessage"/> itself. A request that times out throws a <see
    /// cref="TaskCanceledException"/> whose inner exception is a <see cref="TimeoutException"/>. A request can have a timeout of its own (see
    /// <see cref="SetRequestTimeout(HttpRequestMessage, TimeSpan)"/>). If a parent client exists, returns the parent's value by default; otherwise 100
    /// seconds.
    /// </summary>
    /// <remarks>
    /// <see cref="Timeout.InfiniteTimeSpan"/> and values longer than <see cref="int.MaxValue"/> milliseconds (about 24.8 days), such as
    /// <see cref="TimeSpan.MaxValue"/>, mean no timeout. Requests throw an <see cref="InvalidOperationException"/> for zero, and for negative values other
    /// than <see cref="Timeout.InfiniteTimeSpan"/>. Hub connections are not affected.
    /// </remarks>
    protected virtual TimeSpan RequestTimeout => _parentClient?.RequestTimeout ?? DefaultRequestTimeout;

    /// <summary>
    /// Gets the HTTP message handler that sends the client's requests instead of the handler that all clients share, or <see langword="null"/> for the shared
    /// one. It can be set in the constructor of a derived client. If a parent client exists and the client doesn't set one, returns the parent's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler receives each request with the headers the client set, such as the session cookie and the user agent, and its response is handled like
    /// a response from the shared handler, which doesn't follow redirects or handle cookies: a handler that does either changes how the client handles
    /// redirects and the session cookie. The client doesn't dispose it.
    /// </para>
    /// <para>
    /// Hub connections don't use it. <see cref="ApiErrorResponse.Create(ApiException)"/> creates a response in the toolkit's error format.
    /// </para>
    /// </remarks>
    protected HttpMessageHandler? HttpHandler
    {
        get => field ?? _parentClient?.HttpHandler;
        init {
            field = value;
            _handlerHttpClient = value is null ? null : new HttpClient(value, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    /// <summary>
    /// Gets a value indicating whether the current session is persistent, which means its token is passed to the session token changed callback to be
    /// stored. A session is persistent if its token was passed to the constructor, or if the service issued its cookie with an expiry, as it does for
    /// persistent sessions.
    /// </summary>
    protected bool IsPersistentSession => _sessionState.IsPersistent;

    /// <summary>
    /// Gets the current session token.
    /// </summary>
    protected string? SessionToken => _sessionState.Token;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class using reflection-based JSON serialization with default <see
    /// cref="JsonSerializerDefaults.Web"/> options.
    /// </summary>
    /// <remarks>
    /// This constructor uses reflection-based JSON serialization which is not compatible with trimming. To support trimming, use a constructor that
    /// accepts a source-generated <see cref="IJsonTypeInfoResolver"/> (e.g. a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>).
    /// </remarks>
    [RequiresUnreferencedCode(SerializationUnreferencedCodeMessage)]
    protected ApiClientBase() : this(new JsonSerializerOptions(JsonSerializerDefaults.Web), sessionState: new SessionState(null, null))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class using reflection-based JSON serialization with default <see
    /// cref="JsonSerializerDefaults.Web"/> options, the specified session token and session token changed callback.
    /// </summary>
    /// <param name="sessionToken">The token of a persistent session to continue, such as one stored by <paramref name="sessionTokenChanged"/>.</param>
    /// <param name="sessionTokenChanged">A callback that stores the token of a persistent session. It is called with the token when a persistent session
    /// starts or its token changes, and with <see langword="null"/> when the stored token should be deleted. An exception that it throws propagates to
    /// the request or call that changed the token.</param>
    /// <remarks>
    /// This constructor uses reflection-based JSON serialization which is not compatible with trimming. To support trimming, use a constructor that
    /// accepts a source-generated <see cref="IJsonTypeInfoResolver"/> (e.g. a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>).
    /// </remarks>
    [RequiresUnreferencedCode(SerializationUnreferencedCodeMessage)]
    protected ApiClientBase(string? sessionToken, Action<string?>? sessionTokenChanged)
        : this(new JsonSerializerOptions(JsonSerializerDefaults.Web), sessionState: new SessionState(sessionToken, sessionTokenChanged))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class using the specified JSON serializer options, with an optional session token and
    /// session token changed callback.
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
    protected ApiClientBase(JsonSerializerOptions serializerOptions, string? sessionToken = null, Action<string?>? sessionTokenChanged = null)
        : this(serializerOptions, sessionState: new SessionState(sessionToken, sessionTokenChanged))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class using JSON serialization backed by the specified source-generated type
    /// information resolver (e.g. a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>). This constructor is compatible with trimming.
    /// </summary>
    /// <param name="typeInfoResolver">The source-generated JSON type information resolver used for serialization and deserialization.</param>
    /// <param name="configureOptions">An optional callback that further configures the JSON serializer options created from the resolver.</param>
    /// <inheritdoc cref="ApiClientBase(IJsonTypeInfoResolver, string?, Action{string?}?, Action{JsonSerializerOptions}?)" path="/remarks"/>
    protected ApiClientBase(IJsonTypeInfoResolver typeInfoResolver, Action<JsonSerializerOptions>? configureOptions = null)
        : this(typeInfoResolver, null, null, configureOptions)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class using JSON serialization backed by the specified source-generated type
    /// information resolver (e.g. a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>), with the specified session token and session token
    /// changed callback. This constructor is compatible with trimming.
    /// </summary>
    /// <param name="typeInfoResolver">The source-generated JSON type information resolver used for serialization and deserialization.</param>
    /// <param name="sessionToken">The token of a persistent session to continue, such as one stored by <paramref name="sessionTokenChanged"/>.</param>
    /// <param name="sessionTokenChanged">A callback that stores the token of a persistent session. It is called with the token when a persistent session
    /// starts or its token changes, and with <see langword="null"/> when the stored token should be deleted. An exception that it throws propagates to
    /// the request or call that changed the token.</param>
    /// <param name="configureOptions">An optional callback that further configures the JSON serializer options created from the resolver.</param>
    /// <remarks>
    /// The options have <see cref="JsonSerializerDefaults.Web"/> defaults and the API null handling (see <see
    /// cref="ApiNullHandlingExtensions.ConfigureApiNullHandling(JsonSerializerOptions)"/>). <paramref name="configureOptions"/> runs after the null handling
    /// is applied, so the settings it makes take precedence, and resolvers it adds are not covered by the null handling unless it applies it again.
    /// </remarks>
    protected ApiClientBase(
        IJsonTypeInfoResolver typeInfoResolver,
        string? sessionToken,
        Action<string?>? sessionTokenChanged,
        Action<JsonSerializerOptions>? configureOptions = null)
        : this(CreateOptions(typeInfoResolver, configureOptions), sessionState: new SessionState(sessionToken, sessionTokenChanged))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class that shares the session state and JSON serializer options with the specified
    /// parent client, and by default its base address, request timeout, user agent, session cookie name, user ID precondition query name, user ID
    /// precondition and HTTP handler.
    /// </summary>
    /// <param name="parent">The parent client.</param>
    /// <param name="basePath">A path that this client's base address adds to the parent's, such as <c>$"repos/{repoId}/"</c>. A trailing slash is
    /// added if it has none. Default is no path, which uses the parent's base address.</param>
    /// <param name="userIdPrecondition">The ID of the user that this client's requests are for. It is added to the query string of every request and hub
    /// connection as the user ID precondition (see <see cref="UserIdPreconditionQueryName"/>), formatted like a query string value: a string as it is,
    /// and other values with the invariant culture, such as <c>42</c> for an <see cref="int"/>. Default is <see langword="null"/>, which uses the
    /// parent's.</param>
    protected ApiClientBase(ApiClientBase parent, InterpolatedApiPathHandler basePath = default, object? userIdPrecondition = null)
    {
        SerializerOptions = parent.SerializerOptions;
        _sessionState = parent._sessionState;
        _parentClient = parent;
        _userIdPrecondition = ApiValueFormatter.Format(userIdPrecondition, null) ?? parent._userIdPrecondition;

        string path = basePath.ToString();

        if (path.Length > 0)
            _basePath = path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/";
    }

    private ApiClientBase(JsonSerializerOptions serializerOptions, SessionState sessionState)
    {
        SerializerOptions = serializerOptions;
        _sessionState = sessionState;
        _sessionState.Owner = this;
    }

    /// <summary>
    /// Creates a user agent of the form of the default <see cref="UserAgent"/>: the specified product name and version followed by the operating system,
    /// such as <c>MyApp/2.1.0 (Microsoft Windows 10.0.26100)</c>. Parentheses, backslashes and characters other than printable ASCII are replaced in the
    /// description of the operating system, which is left out if it is empty.
    /// </summary>
    /// <param name="productName">The name of the product. It must be a token: one or more ASCII letters, digits and characters out of
    /// <c>!#$%&amp;'*+-.^_`|~</c>.</param>
    /// <param name="productVersion">The version of the product, such as <c>2.1.0</c>. It must be a token, like the name.</param>
    /// <exception cref="ArgumentException"><paramref name="productName"/> or <paramref name="productVersion"/> is not a token.</exception>
    protected static string CreateUserAgent(string productName, string productVersion)
    {
        return CreateUserAgent(productName, productVersion, RuntimeInformation.OSDescription);
    }

    /// <summary>
    /// Creates a user agent from the product name and version and the specified operating system description (see
    /// <see cref="CreateUserAgent(string, string)"/>).
    /// </summary>
    internal static string CreateUserAgent(string productName, string productVersion, string osDescription)
    {
        if (!IsToken(productName))
            throw new ArgumentException($"The product name '{productName}' is not a token, which a user agent requires.", nameof(productName));

        if (!IsToken(productVersion))
            throw new ArgumentException($"The product version '{productVersion}' is not a token, which a user agent requires.", nameof(productVersion));

        string os = new([.. osDescription.Trim().Select(c => c switch {
            '(' => '[',
            ')' => ']',
            '\\' => '/',
            >= ' ' and <= '~' => c,
            _ => '?',
        })]);

        return os.Length > 0 ? $"{productName}/{productVersion} ({os})" : $"{productName}/{productVersion}";
    }

    /// <summary>
    /// Creates the default user agent from the name and version of the entry assembly and the operating system description (see
    /// <see cref="CreateUserAgent(string, string)"/>). The name and version of this library are used if the entry assembly is unknown or its name is not a
    /// token.
    /// </summary>
    internal static string CreateDefaultUserAgent(AssemblyName? entryAssemblyName, string osDescription)
    {
        var name = entryAssemblyName?.Name is { } entryName && IsToken(entryName) ? entryAssemblyName : typeof(ApiClientBase).Assembly.GetName();
        return CreateUserAgent(name.Name!, name.Version?.ToString() ?? "1.0", osDescription);
    }

    /// <summary>
    /// Gets a value indicating whether the value is a token (RFC 9110), as the product name and version of a user agent must be.
    /// </summary>
    private static bool IsToken(string value) => value.Length > 0 && value.All(c =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or
        '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');

    private static HttpClient CreateHttpClient()
    {
        var client = SharedHttpHandler is not null ? new HttpClient(SharedHttpHandler) : new HttpClient();

        // Each client applies its own request timeout.
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private static JsonSerializerOptions CreateOptions(IJsonTypeInfoResolver typeInfoResolver, Action<JsonSerializerOptions>? configureOptions)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = typeInfoResolver }.ConfigureApiNullHandling();
        configureOptions?.Invoke(options);

        return options;
    }

    /// <summary>
    /// Creates an API request with the specified HTTP method, path and query string parameters.
    /// </summary>
    /// <param name="method">The HTTP method for the request (e.g., GET, POST).</param>
    /// <param name="path">The path of the request, relative to the base address.</param>
    /// <param name="query">The query string parameters, such as <c>[("page", 2), ("filter", filter)]</c>. Parameters with a <see langword="null"/>
    /// value are left out.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not valid or leads outside the base address.</exception>
    protected virtual HttpRequestMessage CreateRequest(
        HttpMethod method, InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default)
    {
        var url = GetApiUrl(path, query);
        var request = new HttpRequestMessage(method, url);

#if NET
        if (OperatingSystem.IsBrowser())
        {
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        }
        else
#endif
        {
            request.Headers.UserAgent.ParseAdd(UserAgent);

            if (_sessionState.Token is not null)
                request.Headers.Add("Cookie", $"{SessionCookieName}={_sessionState.Token}");
        }

        return request;
    }

    /// <summary>
    /// Ends the session on this device: drops the session token and calls the session token changed callback with <see langword="null"/>, so that a stored
    /// token is deleted, and then <see cref="OnSessionEnded"/> if there was a token. This does not end the session on the service. Does nothing on browser
    /// platforms, where the browser holds the session cookie.
    /// </summary>
    protected void ClearSessionToken()
    {
        if (OperatingSystem.IsBrowser())
            return;

        bool ended = false;

        try
        {
            _sessionState.Clear(ref ended);
        }
        finally
        {
            if (ended)
                _sessionState.Owner.OnSessionEnded();
        }
    }

    /// <summary>
    /// Called when the session ends on this device: when a response clears the session cookie, when a request, streaming response or hub connection that
    /// used the current session token fails with a 401 with the <see cref="ApiErrorCodes.SignInRequired"/> error code, when the service reports that the
    /// session of a hub connection that used the current session token ended, and when <see cref="ClearSessionToken"/> drops a token. On browser platforms,
    /// where the browser holds the session cookie and the client doesn't know the session, it is called instead for every such 401 and report of a request
    /// that began, or a hub connection that last connected, after a session last started (see <see cref="NotifySessionStarted"/>): the reports of the
    /// others concern the session that the new one replaced. There, a hub connection with <c>ApiHubConnection.StayConnected</c> whose session ended with
    /// the <see cref="ApiErrorCodes.SignInRequired"/> error code reports it only once its attempt with the browser's session cookie is refused with it too,
    /// since another tab may have signed in (see the SignalR client library). Does nothing by default.
    /// </summary>
    /// <remarks>
    /// It is called on the client that created the session: the client without a parent, whose child clients share its session. It runs after the session
    /// token changed callback, as part of the request or call that ended the session, even if the callback throws, and an exception that it throws
    /// propagates to that request or call.
    /// </remarks>
    protected virtual void OnSessionEnded()
    {
    }

    /// <summary>
    /// Reports that a session started on this device, such as with a sign-in, to the hub connections that wait for a new session after theirs ended (see
    /// <c>ApiHubConnection.StayConnected</c> in the SignalR client library), which then start again. It reports to the connections of all the clients that
    /// share the session. On non-browser platforms, the client also reports a session itself when a response sets the session cookie while it has none,
    /// and when a sign-in's response, which the service marks with a <c>Session-Change</c> header, sets it. On browser platforms, where the browser holds
    /// the session cookie, only this method reports one.
    /// </summary>
    protected void NotifySessionStarted() => _sessionState.Start();

    /// <summary>
    /// Gets a task that completes when a session next starts on this device (see <see cref="NotifySessionStarted"/>).
    /// </summary>
    internal Task NextSessionStart => _sessionState.NextStart;

    /// <summary>
    /// Waits until a request of a client that shares the session, which begins after this call, gets a successful response from the host of its base
    /// address with the specified user ID precondition in its URL, or with any user ID precondition if it is <see langword="null"/>. Hub connections in the
    /// browser take such a response as a cue that the browser holds a session again (see <c>ApiHubConnection.StayConnected</c> in the SignalR client
    /// library). The wait ends with the token, which cancels the task.
    /// </summary>
    internal Task WaitForRequestCueAsync(string? userIdPrecondition, CancellationToken cancellationToken) =>
        _sessionState.WaitForCueAsync(userIdPrecondition, cancellationToken);

    /// <summary>
    /// Gets the user ID precondition that the URL carries, as the service reads it: the value of the query string parameter named
    /// <see cref="UserIdPreconditionQueryName"/>, or <see langword="null"/> if the URL has none, has more than one, or has an empty one, which the service
    /// refuses.
    /// </summary>
    internal string? GetUserIdPrecondition(Uri? url)
    {
        if (url is not { IsAbsoluteUri: true } || url.Query.Length <= 1)
            return null;

        string name = UserIdPreconditionQueryName;
        string? userIdPrecondition = null;

        foreach (string param in url.Query.Substring(1).Split('&'))
        {
            int eqIndex = param.IndexOf('=');

            if (!string.Equals(Unescape(eqIndex < 0 ? param : param[..eqIndex]), name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (userIdPrecondition is not null)
                return null;

            userIdPrecondition = eqIndex < 0 ? string.Empty : Unescape(param[(eqIndex + 1)..]);
        }

        return userIdPrecondition is { Length: > 0 } ? userIdPrecondition : null;

        static string Unescape(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    /// <summary>
    /// Ends the session on this device if the error says that the session of a request or hub connection that used the specified token has ended (a 401 with
    /// the <see cref="ApiErrorCodes.SignInRequired"/> error code), as a cleared session cookie does. Hub errors and the errors of a streaming response that
    /// has started can't clear the cookie. Does nothing if the session token has changed since. On browser platforms, which have no session token, calls
    /// <see cref="OnSessionEnded"/> for every such error of a request that began, or a hub connection that last connected, in the current session (see
    /// <see cref="EndSession(bool, string?, Task)"/>).
    /// </summary>
    internal void OnApiError(ApiException exception, string? sessionToken, Task sessionStart) =>
        OnApiError(OperatingSystem.IsBrowser(), exception, sessionToken, sessionStart);

    /// <summary>
    /// Ends the session on this device if the error says that the session of a request or hub connection that used the specified token has ended, judged as
    /// in the browser if <paramref name="isBrowser"/> is <see langword="true"/> (see <see cref="OnApiError(ApiException, string?, Task)"/>).
    /// </summary>
    internal void OnApiError(bool isBrowser, ApiException exception, string? sessionToken, Task sessionStart)
    {
        if (exception is UnauthorizedApiException { ErrorCode: ApiErrorCodes.SignInRequired })
            EndSession(isBrowser, sessionToken, sessionStart);
    }

    /// <summary>
    /// Ends the session on this device, as a cleared session cookie does, because the session of a request or hub connection that used the specified token
    /// has ended (see <see cref="EndSession(bool, string?, Task)"/>).
    /// </summary>
    internal void EndSession(string? sessionToken, Task sessionStart) => EndSession(OperatingSystem.IsBrowser(), sessionToken, sessionStart);

    /// <summary>
    /// Ends the session on this device, as a cleared session cookie does, because the session of a request or hub connection that used the specified token
    /// has ended. On platforms other than the browser, does nothing if the session token has changed since, which tells whether the report concerns the
    /// current session. In the browser, which has no session token, calls <see cref="OnSessionEnded"/> unless a session started since the request began or
    /// the hub connection last connected (<paramref name="sessionStart"/>, the task of <see cref="NextSessionStart"/> then), since the report then concerns
    /// the session that the new one replaced, such as after a sign-in of another user.
    /// </summary>
    internal void EndSession(bool isBrowser, string? sessionToken, Task sessionStart)
    {
        if (isBrowser)
        {
            if (!sessionStart.IsCompleted)
                _sessionState.Owner.OnSessionEnded();

            return;
        }

        if (sessionToken is null)
            return;

        bool ended = false;

        try
        {
            _sessionState.End(sessionToken, ref ended);
        }
        finally
        {
            if (ended)
                _sessionState.Owner.OnSessionEnded();
        }
    }

    /// <summary>
    /// Gets the base address for the API client. Request and hub connection URLs are always built by combining this address with the path of the request
    /// or hub connection, and must be under it. An address whose path has no trailing slash is used as if it had one, so that
    /// <c>https://example.com/api</c> works like <c>https://example.com/api/</c>. If a parent client exists, returns the parent's base address with the
    /// base path this client was created with by default. A client without a parent must override this method.
    /// </summary>
    /// <exception cref="InvalidOperationException">The client has no parent and does not override this method, or its base path is not valid or leads
    /// outside the parent's base address.</exception>
    protected virtual Uri GetBaseAddress()
    {
        if (_parentClient is null)
        {
            throw new InvalidOperationException(
                $"API client '{GetType()}' has no parent client, so it must override {nameof(GetBaseAddress)}() to provide its base address.");
        }

        var parentAddress = WithTrailingSlash(_parentClient.GetBaseAddress());

        if (_basePath is null)
            return parentAddress;

        Uri address;

        try
        {
            address = new Uri(parentAddress, _basePath);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException($"The base path '{_basePath}' of API client '{GetType()}' is not a valid relative path or absolute URI.", ex);
        }

        if (!parentAddress.IsBaseOf(address))
        {
            throw new InvalidOperationException(
                $"The base path '{_basePath}' of API client '{GetType()}' {DescribeOutside(parentAddress, address)} its parent's base address.");
        }

        return address;
    }

    /// <summary>
    /// Adds a trailing slash to the path of an address that has none, so that paths are resolved under its last segment, as child clients do with their
    /// base paths.
    /// </summary>
    private static Uri WithTrailingSlash(Uri address)
    {
        if (address.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            return address;

        return new Uri(address.GetLeftPart(UriPartial.Path) + "/" + address.Query);
    }

    /// <summary>
    /// Describes how a URI that is not under a base address relates to it, for an error message that continues with the base address.
    /// </summary>
    private static string DescribeOutside(Uri baseAddress, Uri uri)
    {
        return string.Equals(uri.Scheme, baseAddress.Scheme, StringComparison.OrdinalIgnoreCase) ? "leads outside" : "is an absolute URI outside";
    }

    /// <summary>
    /// Sends an API request.
    /// </summary>
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)" path="/exception[@cref!='T:System.ArgumentException']"/>
    protected Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        return SendAsync<VoidApiResponse>(request, null, cancellationToken);
    }

    /// <summary>
    /// Sends an API request with the specified content.
    /// </summary>
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)" path="/exception"/>
    protected Task SendAsync(HttpRequestMessage request, object? content, CancellationToken cancellationToken = default)
    {
        return SendAsync<VoidApiResponse>(request, content, cancellationToken);
    }

    /// <summary>
    /// Sends an API request and returns the response.
    /// </summary>
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)" path="/exception[@cref!='T:System.ArgumentException']"/>
    protected async Task<TResponse> SendAsync<TResponse>(HttpRequestMessage request, CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        return await SendAsync<TResponse>(request, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an API request with the specified content and returns the response.
    /// </summary>
    /// <exception cref="HttpRequestException">An error occurred while sending the request or reading the response.</exception>
    /// <exception cref="BadRequestApiException">A 400 (Bad Request) response was returned.</exception>
    /// <exception cref="UnauthorizedApiException">A 401 (Unauthorized) response was returned.</exception>
    /// <exception cref="ForbiddenApiException">A 403 (Forbidden) response was returned.</exception>
    /// <exception cref="NotFoundApiException">A 404 (Not Found) response was returned.</exception>
    /// <exception cref="ConflictApiException">A 409 (Conflict) response was returned.</exception>
    /// <exception cref="UserChangedApiException">A 412 (Precondition Failed) response was returned.</exception>
    /// <exception cref="ContentTooLargeApiException">A 413 (Content Too Large) response was returned.</exception>
    /// <exception cref="UnsupportedMediaTypeApiException">A 415 (Unsupported Media Type) response was returned.</exception>
    /// <exception cref="ValidationApiException">A 422 (Unprocessable Content) response was returned.</exception>
    /// <exception cref="UserRequiredApiException">A 428 (Precondition Required) response was returned.</exception>
    /// <exception cref="TooManyRequestsApiException">A 429 (Too Many Requests) response was returned.</exception>
    /// <exception cref="ServerErrorApiException">A 500 (Internal Server Error) response was returned.</exception>
    /// <exception cref="ServiceUnavailableApiException">A 503 (Service Unavailable) response was returned.</exception>
    /// <exception cref="ApiException">Any other error response or a <c>3xx</c> response was returned. Redirects are not followed, except on browser
    /// platforms (see <see cref="ApiException.Location"/>).</exception>
    /// <exception cref="JsonException">The response content is not valid JSON for the response type, such as a missing or <see langword="null"/> value
    /// where the type does not allow one, or a body that is <see langword="null"/>.</exception>
    /// <exception cref="TaskCanceledException">The request did not complete within its timeout (see <see cref="RequestTimeout"/> and
    /// <see cref="SetRequestTimeout(HttpRequestMessage, TimeSpan)"/>). The inner exception is a <see cref="TimeoutException"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="RequestTimeout"/> is zero, or negative other than <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The content is not <see langword="null"/> and the request already has content.</exception>
    protected virtual async Task<TResponse> SendAsync<TResponse>(HttpRequestMessage request, object? content, CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        var timeout = GetRequestTimeout(request);
        using var timeoutSource = CreateTimeoutSource(timeout, cancellationToken);

        try
        {
            return await SendAndReadAsync<TResponse>(request, content, timeoutSource?.Token ?? cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutSource?.IsCancellationRequested is true && !cancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException(timeout, ex);
        }
    }

    /// <summary>
    /// Sends a GET request.
    /// </summary>
    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task GetAsync(
        InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default, CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateRequest(HttpMethod.Get, path, query), cancellationToken);
    }

    /// <inheritdoc cref="GetAsync(InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task GetAsync(InterpolatedApiPathHandler path, CancellationToken cancellationToken) => GetAsync(path, default, cancellationToken);

    /// <summary>
    /// Sends a GET request and returns the response.
    /// </summary>
    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> GetAsync<TResponse>(
        InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default, CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        return SendAsync<TResponse>(CreateRequest(HttpMethod.Get, path, query), null, cancellationToken);
    }

    /// <inheritdoc cref="GetAsync{TResponse}(InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> GetAsync<TResponse>(InterpolatedApiPathHandler path, CancellationToken cancellationToken)
        where TResponse : notnull
    {
        return GetAsync<TResponse>(path, default, cancellationToken);
    }

    /// <summary>
    /// Sends a POST request.
    /// </summary>
    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task PostAsync(
        InterpolatedApiPathHandler path,
        object? content = null,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateRequest(HttpMethod.Post, path, query), content, cancellationToken);
    }

    /// <inheritdoc cref="PostAsync(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task PostAsync(InterpolatedApiPathHandler path, object? content, CancellationToken cancellationToken) =>
        PostAsync(path, content, default, cancellationToken);

    /// <inheritdoc cref="PostAsync(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task PostAsync(InterpolatedApiPathHandler path, CancellationToken cancellationToken) => PostAsync(path, null, default, cancellationToken);

    /// <summary>
    /// Sends a POST request and returns the response.
    /// </summary>
    /// <typeparam name="TResponse">The type to read the response as: a type to deserialize the JSON body as, <see cref="string"/> for the body as text, or
    /// <see cref="HttpResponseMessage"/> for the response itself, which the caller must dispose.</typeparam>
    /// <param name="path">The path of the request, relative to the base address.</param>
    /// <param name="content">The content of the request: serialized as JSON, unless it is an <see cref="HttpContent"/> or a string. Default is
    /// <see langword="null"/>, which sends no content.</param>
    /// <param name="query">The query string parameters. Parameters with a <see langword="null"/> value are left out.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not valid or leads outside the base address.</exception>
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)" path="/exception[@cref!='T:System.ArgumentException']"/>
    protected Task<TResponse> PostAsync<TResponse>(
        InterpolatedApiPathHandler path,
        object? content = null,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        return SendAsync<TResponse>(CreateRequest(HttpMethod.Post, path, query), content, cancellationToken);
    }

    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> PostAsync<TResponse>(InterpolatedApiPathHandler path, object? content, CancellationToken cancellationToken)
        where TResponse : notnull
    {
        return PostAsync<TResponse>(path, content, default, cancellationToken);
    }

    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> PostAsync<TResponse>(InterpolatedApiPathHandler path, CancellationToken cancellationToken)
        where TResponse : notnull
    {
        return PostAsync<TResponse>(path, null, default, cancellationToken);
    }

    /// <summary>
    /// Sends a PUT request.
    /// </summary>
    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task PutAsync(
        InterpolatedApiPathHandler path,
        object? content = null,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateRequest(HttpMethod.Put, path, query), content, cancellationToken);
    }

    /// <inheritdoc cref="PutAsync(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task PutAsync(InterpolatedApiPathHandler path, object? content, CancellationToken cancellationToken) =>
        PutAsync(path, content, default, cancellationToken);

    /// <inheritdoc cref="PutAsync(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task PutAsync(InterpolatedApiPathHandler path, CancellationToken cancellationToken) => PutAsync(path, null, default, cancellationToken);

    /// <summary>
    /// Sends a PUT request and returns the response.
    /// </summary>
    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> PutAsync<TResponse>(
        InterpolatedApiPathHandler path,
        object? content = null,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        return SendAsync<TResponse>(CreateRequest(HttpMethod.Put, path, query), content, cancellationToken);
    }

    /// <inheritdoc cref="PutAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> PutAsync<TResponse>(InterpolatedApiPathHandler path, object? content, CancellationToken cancellationToken)
        where TResponse : notnull
    {
        return PutAsync<TResponse>(path, content, default, cancellationToken);
    }

    /// <inheritdoc cref="PutAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> PutAsync<TResponse>(InterpolatedApiPathHandler path, CancellationToken cancellationToken)
        where TResponse : notnull
    {
        return PutAsync<TResponse>(path, null, default, cancellationToken);
    }

    /// <summary>
    /// Sends a PATCH request.
    /// </summary>
    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task PatchAsync(
        InterpolatedApiPathHandler path,
        object? content = null,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateRequest(PatchMethod, path, query), content, cancellationToken);
    }

    /// <inheritdoc cref="PatchAsync(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task PatchAsync(InterpolatedApiPathHandler path, object? content, CancellationToken cancellationToken) =>
        PatchAsync(path, content, default, cancellationToken);

    /// <inheritdoc cref="PatchAsync(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task PatchAsync(InterpolatedApiPathHandler path, CancellationToken cancellationToken) => PatchAsync(path, null, default, cancellationToken);

    /// <summary>
    /// Sends a PATCH request and returns the response.
    /// </summary>
    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> PatchAsync<TResponse>(
        InterpolatedApiPathHandler path,
        object? content = null,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        return SendAsync<TResponse>(CreateRequest(PatchMethod, path, query), content, cancellationToken);
    }

    /// <inheritdoc cref="PatchAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> PatchAsync<TResponse>(InterpolatedApiPathHandler path, object? content, CancellationToken cancellationToken)
        where TResponse : notnull
    {
        return PatchAsync<TResponse>(path, content, default, cancellationToken);
    }

    /// <inheritdoc cref="PatchAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> PatchAsync<TResponse>(InterpolatedApiPathHandler path, CancellationToken cancellationToken)
        where TResponse : notnull
    {
        return PatchAsync<TResponse>(path, null, default, cancellationToken);
    }

    /// <summary>
    /// Sends a DELETE request.
    /// </summary>
    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task DeleteAsync(
        InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default, CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateRequest(HttpMethod.Delete, path, query), cancellationToken);
    }

    /// <inheritdoc cref="DeleteAsync(InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task DeleteAsync(InterpolatedApiPathHandler path, CancellationToken cancellationToken) => DeleteAsync(path, default, cancellationToken);

    /// <summary>
    /// Sends a DELETE request and returns the response.
    /// </summary>
    /// <inheritdoc cref="PostAsync{TResponse}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> DeleteAsync<TResponse>(
        InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default, CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        return SendAsync<TResponse>(CreateRequest(HttpMethod.Delete, path, query), null, cancellationToken);
    }

    /// <inheritdoc cref="DeleteAsync{TResponse}(InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected Task<TResponse> DeleteAsync<TResponse>(InterpolatedApiPathHandler path, CancellationToken cancellationToken)
        where TResponse : notnull
    {
        return DeleteAsync<TResponse>(path, default, cancellationToken);
    }

    /// <summary>
    /// Sends an API request and returns a streaming response (see <see cref="StreamingResponse"/>).
    /// </summary>
    /// <inheritdoc cref="SendStreamingAsync{TItem}(HttpRequestMessage, object?, CancellationToken)" path="/exception"/>
    protected IAsyncEnumerable<TItem> SendStreamingAsync<TItem>(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        return SendStreamingAsync<TItem>(request, null, cancellationToken);
    }

    /// <summary>
    /// Sends an API request with the specified content and returns a streaming response (see <see cref="StreamingResponse"/>). Exceptions reported in the
    /// response stream are thrown from the enumeration as the corresponding <see cref="ApiException"/>. Null items are yielded as <see langword="null"/>,
    /// whether or not the item type is nullable.
    /// </summary>
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)" path="/exception"/>
    /// <exception cref="IOException">The streaming response ended before completion. On .NET 10 and later, this is an <see
    /// cref="T:System.Net.Http.HttpIOException"/>.</exception>
    /// <exception cref="FormatException">The response was not a streaming response or contained an unrecognized record.</exception>
    /// <exception cref="TaskCanceledException">The response did not start within the request's timeout (see <see cref="RequestTimeout"/> and
    /// <see cref="SetRequestTimeout(HttpRequestMessage, TimeSpan)"/>). The inner exception is a <see cref="TimeoutException"/>.</exception>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = SerializationSuppressionJustification)]
    protected virtual async IAsyncEnumerable<TItem> SendStreamingAsync<TItem>(
        HttpRequestMessage request, object? content, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? sessionToken = GetSessionToken(request);
        var sessionStart = NextSessionStart;
        var activity = StreamActivityCallback.Value;
        var timeout = GetRequestTimeout(request);
        HttpResponseMessage response;

        using (var timeoutSource = CreateTimeoutSource(timeout, cancellationToken))
        {
            try
            {
                response = await SendAndCheckAsync(request, content, timeoutSource?.Token ?? cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (timeoutSource?.IsCancellationRequested is true && !cancellationToken.IsCancellationRequested)
            {
                throw CreateTimeoutException(timeout, ex);
            }
        }

#if NET
        Stream? body = null;
        bool isAtEnd = false;
        bool isStopped = false;
#endif

        try
        {
            string? mediaType = response.Content.Headers.ContentType?.MediaType;

            if (!string.Equals(mediaType, StreamingResponse.MediaType, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Expected a '{StreamingResponse.MediaType}' streaming response but received '{mediaType ?? "no"}' content.");

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

#if NET
            body = stream;
#else
            // Disposing the response disposes the stream.
            stream = new CancellableResponseStream(stream, response);
#endif

            if (activity is not null)
                stream = new ActivityReportingStream(stream, activity);

            var itemTypeInfo = SerializerOptions.GetTypeInfo<TItem>();
            string? userIdPrecondition = GetUserIdPrecondition(request.RequestUri);
            var items = StreamingResponse.ReadItemsAsync(stream, itemTypeInfo, userIdPrecondition, cancellationToken).ConfigureAwait(false).GetAsyncEnumerator();

            try
            {
                while (true)
                {
                    bool hasItem;

                    try
                    {
                        hasItem = await items.MoveNextAsync();
                    }
                    catch (ApiException ex)
                    {
#if NET
                        // An error record ends the stream.
                        isAtEnd = true;
#endif
                        OnApiError(ex, sessionToken, sessionStart);
                        throw;
                    }

                    if (!hasItem)
                    {
#if NET
                        isAtEnd = true;
#endif
                        break;
                    }

#if NET
                    isStopped = true;
#endif
                    yield return items.Current;
#if NET
                    isStopped = false;
#endif
                }
            }
            finally
            {
                await items.DisposeAsync();
            }
        }
        finally
        {
#if NET
            // Nothing follows the end record or an error record but the end of the body. In the browser, the rest of a response whose enumeration ended at
            // an item, such as the end record after the item that the caller waited for, is read in the background, so that the request isn't aborted.
            if (isAtEnd)
            {
                await ReadToTheEndAsync(body!, cancellationToken).ConfigureAwait(false);
                await DisposeResponseAsync(body, response).ConfigureAwait(false);
            }
            else if (isStopped && DrainsStoppedResponses && !cancellationToken.IsCancellationRequested)
            {
                _ = DrainAndDisposeResponseAsync(body!, response);
            }
            else
            {
                await DisposeResponseAsync(body, response).ConfigureAwait(false);
            }
#else
            DisposeResponse(response);
#endif
        }
    }

    /// <summary>
    /// Sends a GET request and returns a streaming response (see <see cref="StreamingResponse"/>).
    /// </summary>
    /// <inheritdoc cref="PostStreamingAsync{TItem}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected IAsyncEnumerable<TItem> GetStreamingAsync<TItem>(
        InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default, CancellationToken cancellationToken = default)
    {
        return CreateAndSendStreamingAsync<TItem>(HttpMethod.Get, path.ToString(), query.ToArray(), null, cancellationToken);
    }

    /// <inheritdoc cref="GetStreamingAsync{TItem}(InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected IAsyncEnumerable<TItem> GetStreamingAsync<TItem>(InterpolatedApiPathHandler path, CancellationToken cancellationToken)
    {
        return GetStreamingAsync<TItem>(path, default, cancellationToken);
    }

    /// <summary>
    /// Sends a POST request and returns a streaming response (see <see cref="StreamingResponse"/>).
    /// </summary>
    /// <typeparam name="TItem">The type of the streamed items.</typeparam>
    /// <param name="path">The path of the request, relative to the base address.</param>
    /// <param name="content">The content of the request: serialized as JSON, unless it is an <see cref="HttpContent"/> or a string. Default is
    /// <see langword="null"/>, which sends no content. An <see cref="HttpContent"/> is disposed with the request, so it can only be sent once.</param>
    /// <param name="query">The query string parameters. Parameters with a <see langword="null"/> value are left out.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <remarks>
    /// The request is created with <see cref="CreateRequest(HttpMethod, InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}})"/> and sent
    /// when the enumeration starts, and again for each enumeration. Exceptions reported in the response stream are thrown from the enumeration as the
    /// corresponding <see cref="ApiException"/>. Null items are yielded as <see langword="null"/>, whether or not the item type is nullable.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not valid or leads outside the base address. Thrown when the enumeration starts.
    /// </exception>
    /// <inheritdoc cref="SendStreamingAsync{TItem}(HttpRequestMessage, object?, CancellationToken)" path="/exception[@cref!='T:System.ArgumentException']"/>
    protected IAsyncEnumerable<TItem> PostStreamingAsync<TItem>(
        InterpolatedApiPathHandler path,
        object? content = null,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        CancellationToken cancellationToken = default)
    {
        return CreateAndSendStreamingAsync<TItem>(HttpMethod.Post, path.ToString(), query.ToArray(), content, cancellationToken);
    }

    /// <inheritdoc cref="PostStreamingAsync{TItem}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected IAsyncEnumerable<TItem> PostStreamingAsync<TItem>(InterpolatedApiPathHandler path, object? content, CancellationToken cancellationToken)
    {
        return PostStreamingAsync<TItem>(path, content, default, cancellationToken);
    }

    /// <inheritdoc cref="PostStreamingAsync{TItem}(InterpolatedApiPathHandler, object?, ReadOnlySpan{ValueTuple{string, object}}, CancellationToken)"/>
    protected IAsyncEnumerable<TItem> PostStreamingAsync<TItem>(InterpolatedApiPathHandler path, CancellationToken cancellationToken)
    {
        return PostStreamingAsync<TItem>(path, null, default, cancellationToken);
    }

    /// <summary>
    /// Creates a request when the enumeration starts and returns its streaming response. The path is used as it is, since its values are already escaped.
    /// </summary>
    private async IAsyncEnumerable<TItem> CreateAndSendStreamingAsync<TItem>(
        HttpMethod method, string path, (string Name, object? Value)[] query, object? content, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = CreateRequest(method, path, query);

        await foreach (var item in SendStreamingAsync<TItem>(request, content, cancellationToken).ConfigureAwait(false))
            yield return item;
    }

#if NET
    /// <summary>
    /// Gets a value indicating whether the rest of a streaming response whose enumeration ends at an item is read in the background before the response is
    /// disposed, which it is in the browser. Tests set it to check the browser's behavior on other platforms.
    /// </summary>
    internal bool DrainsStoppedResponses { get; init; } = OperatingSystem.IsBrowser();

    /// <summary>
    /// Gets how long the client reads the rest of the body of a response before it disposes the response (see <see cref="ReadToTheEndAsync"/>). Tests set
    /// it to a shorter time.
    /// </summary>
    internal TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads the rest of the body of a response that the client has read, before the response is disposed, for up to <see cref="DrainTimeout"/>. In the
    /// browser, disposing a response whose body was not read to the end aborts its request, which the browser reports as a failed request. A read that
    /// fails or takes longer doesn't change the result of the request, also on a platform whose reads don't stop when they are cancelled: the read is then
    /// left to the disposal of the response.
    /// </summary>
    private async Task ReadToTheEndAsync(Stream body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var read = body.CopyToAsync(Stream.Null, timeout.Token);

            if (await Task.WhenAny(read, Task.Delay(DrainTimeout, timeout.Token)).ConfigureAwait(false) != read)
            {
                timeout.Cancel();
                _ = read.ContinueWith(static r => r.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

                return;
            }

            await read.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            // The response is disposed all the same.
        }
    }

    /// <summary>
    /// Reads the rest of the body of a streaming response whose enumeration ended at an item, for up to <see cref="DrainTimeout"/> (see
    /// <see cref="ReadToTheEndAsync(Stream, CancellationToken)"/>), and then disposes the response.
    /// </summary>
    private async Task DrainAndDisposeResponseAsync(Stream body, HttpResponseMessage response)
    {
        try
        {
            await ReadToTheEndAsync(body, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await DisposeResponseAsync(body, response).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the body of a streaming response, if it was opened, and then the response.
    /// </summary>
    private static async ValueTask DisposeResponseAsync(Stream? body, HttpResponseMessage response)
    {
        try
        {
            if (body is not null)
                await body.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }
#endif

    /// <summary>
    /// Disposes a response that the client has read. On .NET Framework, disposing a response whose body has not been read to the end blocks for about
    /// half a second, so the response is disposed off the calling thread there.
    /// </summary>
    private static void DisposeResponse(HttpResponseMessage response)
    {
#if NET
        response.Dispose();
#else
        response.DisposeInBackground();
#endif
    }

    /// <summary>
    /// Sets how long the request can take when it is sent with one of the <c>SendAsync</c> or <c>SendStreamingAsync</c> methods, instead of
    /// <see cref="RequestTimeout"/>. The timeout covers the same part of the request as <see cref="RequestTimeout"/>: from sending it, including its
    /// content, until the response is read, or until the response starts for a streaming response or a request that returns the
    /// <see cref="HttpResponseMessage"/> itself.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="timeout">How long the request can take. <see cref="Timeout.InfiniteTimeSpan"/> and values longer than <see cref="int.MaxValue"/>
    /// milliseconds (about 24.8 days) mean no timeout.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is zero, or negative other than <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    protected static void SetRequestTimeout(HttpRequestMessage request, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be positive or Timeout.InfiniteTimeSpan.");

#if NET
        request.Options.Set(RequestTimeoutOptionKey, timeout);
#else
        request.Properties[RequestTimeoutOptionName] = timeout;
#endif
    }

    /// <summary>
    /// Gets the timeout that was set for a request (see <see cref="SetRequestTimeout(HttpRequestMessage, TimeSpan)"/>), or the <see cref="RequestTimeout"/>.
    /// Disposes the request and throws if the value is not valid.
    /// </summary>
    private TimeSpan GetRequestTimeout(HttpRequestMessage request)
    {
#if NET
        if (request.Options.TryGetValue(RequestTimeoutOptionKey, out var requestTimeout))
            return requestTimeout;
#else
        if (request.Properties.TryGetValue(RequestTimeoutOptionName, out object? value) && value is TimeSpan requestTimeout)
            return requestTimeout;
#endif

        var timeout = RequestTimeout;

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            request.Dispose();

            throw new InvalidOperationException(
                $"The request timeout of API client '{GetType()}' is {timeout}, but must be positive or Timeout.InfiniteTimeSpan.");
        }

        return timeout;
    }

    private static CancellationTokenSource? CreateTimeoutSource(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // CancelAfter throws for longer timeouts on .NET Framework, and for timeouts about twice as long on .NET.
        if (timeout == Timeout.InfiniteTimeSpan || timeout.TotalMilliseconds > int.MaxValue)
            return null;

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);

        return source;
    }

    internal static TaskCanceledException CreateTimeoutException(TimeSpan timeout, OperationCanceledException exception)
    {
        double seconds = timeout.TotalSeconds;
        string message = string.Format(
            CultureInfo.InvariantCulture,
            "The request was canceled because it did not complete within the request timeout of {0} {1}.",
            seconds,
            seconds is 1 ? "second" : "seconds");

        // Matches HttpClient, which reports its own timeout the same way.
        return new TaskCanceledException(message, new TimeoutException(message, exception));
    }

    private HttpClient GetHttpClient() => _handlerHttpClient ?? _parentClient?.GetHttpClient() ?? _httpClient;

    /// <summary>
    /// Sends the request and reads the response as <typeparamref name="TResponse"/> (see <see cref="SendAsync{T}(HttpRequestMessage, object?,
    /// CancellationToken)"/>).
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = SerializationSuppressionJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = SerializationSuppressionJustification)]
    private async Task<TResponse> SendAndReadAsync<TResponse>(HttpRequestMessage request, object? content, CancellationToken cancellationToken)
        where TResponse : notnull
    {
        var response = await SendAndCheckAsync(request, content, cancellationToken).ConfigureAwait(false);

        if (typeof(TResponse) == typeof(HttpResponseMessage))
            return (TResponse)(object)response;

        try
        {
            if (typeof(TResponse) == typeof(VoidApiResponse))
            {
#if NET
                await ReadToTheEndAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                    .ConfigureAwait(false);
#endif
                return default!;
            }

            TResponse? result;

            try
            {
                if (typeof(TResponse) == typeof(string))
                    return (TResponse)(object)await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                var read = response.Content.ReadFromJsonAsync<TResponse>(SerializerOptions, cancellationToken);
#if !NET
                read = read.WaitOrAbortAsync(response.Content, cancellationToken);
#endif
                result = await read.ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw CreateResponseReadException(ex);
            }

            return result ?? throw new JsonException("The response body is null.");
        }
        finally
        {
            DisposeResponse(response);
        }
    }

    /// <summary>
    /// Creates the exception for an error while reading a response body, such as a connection that closed before the body ended. Reading JSON from the
    /// content stream throws such errors as they are, while reading the content as a string already wraps them in an <see cref="HttpRequestException"/>.
    /// </summary>
    private static HttpRequestException CreateResponseReadException(IOException exception)
    {
        const string message = "An error occurred while reading the response.";

#if NET
        var error = exception is HttpIOException httpIOException ? httpIOException.HttpRequestError : HttpRequestError.Unknown;
        return new HttpRequestException(error, message, exception);
#else
        return new HttpRequestException(message, exception);
#endif
    }

    /// <summary>
    /// Sends the request, picks up the session cookie from the response and throws the matching exception for an error response. Returns the response
    /// once its headers are read.
    /// </summary>
    private async Task<HttpResponseMessage> SendAndCheckAsync(HttpRequestMessage request, object? content, CancellationToken cancellationToken)
    {
        string? sessionToken = GetSessionToken(request);
        var sessionStart = NextSessionStart;
        long requestNumber = _sessionState.BeginRequest();
        HttpResponseMessage response;

        try
        {
            SetRequestContent(request, content);
            response = await GetHttpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }

        try
        {
            if (!OperatingSystem.IsBrowser())
                UpdateSessionToken(request, response, sessionToken, IsSessionChange(request, response));

            await ThrowOnErrorResponseAsync(request, response, cancellationToken).ConfigureAwait(false);
            CueHubConnections(request, response, requestNumber);

            return response;
        }
        catch (Exception ex)
        {
            DisposeResponse(response);

            if (ex is ApiException apiException)
                OnApiError(apiException, sessionToken, sessionStart);

            throw;
        }
    }

    /// <summary>
    /// Passes the successful response of a request with a user ID precondition from the host of the base address to the hub connections that wait for
    /// one (see <see cref="WaitForRequestCueAsync"/>).
    /// </summary>
    private void CueHubConnections(HttpRequestMessage request, HttpResponseMessage response, long requestNumber)
    {
        if (!_sessionState.HasCueWaits)
            return;

        var url = (response.RequestMessage ?? request).RequestUri;

        if (IsBaseAddressHost(url) && GetUserIdPrecondition(url) is { } userIdPrecondition)
            _sessionState.Cue(userIdPrecondition, requestNumber);
    }

    /// <summary>
    /// Gets the session token that the request's <c>Cookie</c> header carries, if any.
    /// </summary>
    private string? GetSessionToken(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("Cookie", out var values))
            return null;

        string cookieName = SessionCookieName;

        foreach (string value in values)
        {
            foreach (string cookie in value.Split(';'))
            {
                int eqIndex = cookie.IndexOf('=');

                if (eqIndex > 0 && cookie.AsSpan(0, eqIndex).Trim().SequenceEqual(cookieName.AsSpan()))
                    return cookie[(eqIndex + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Sets the content of the request: an <see cref="HttpContent"/> as it is, a string as text, and anything else as JSON. Does nothing if the content is
    /// <see langword="null"/>.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = SerializationSuppressionJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = SerializationSuppressionJustification)]
    private void SetRequestContent(HttpRequestMessage request, object? content)
    {
        if (content is not null)
        {
            if (request.Content is not null)
                throw new ArgumentException("The request already has content.", nameof(content));

            if (content is HttpContent httpContent)
                request.Content = httpContent;
            else if (content is string strContent)
                request.Content = new StringContent(strContent);
            else
                request.Content = JsonContent.Create(content, options: SerializerOptions);
        }
    }

    /// <summary>
    /// Throws an appropriate API exception if the response indicates an error. The exception for a <c>412</c> carries the user ID precondition of the
    /// request, if it has one (see <see cref="UserChangedApiException.ExpectedUserId"/>).
    /// </summary>
    private async ValueTask ThrowOnErrorResponseAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is >= HttpStatusCode.OK and <= (HttpStatusCode)299)
            return;

        var contentType = response.Content.Headers.ContentType;
        string? errorContentType = contentType?.MediaType;

        bool hasErrorCode = contentType?.Parameters.Any(p =>
            p.Name.Equals("format", StringComparison.OrdinalIgnoreCase) && "error-code".Equals(p.Value, StringComparison.OrdinalIgnoreCase)) ?? false;

        string errorContentString;

        try
        {
            errorContentString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw CreateResponseReadException(ex);
        }

        ResponseExceptionInfo.ParseAndThrow(
            (int)response.StatusCode,
            errorContentString,
            errorContentType,
            hasErrorCode,
            GetRetryAfter(response),
            GetLocation(response),
            GetReferenceId(response),
            GetUserIdPrecondition(request.RequestUri));
    }

    /// <summary>
    /// Gets the reference ID from the response's <c>Reference-Id</c> header, or <see langword="null"/> if the response has no header with a GUID.
    /// </summary>
    private static Guid? GetReferenceId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(ResponseExceptionInfo.ReferenceIdHeaderName, out var values))
            return null;

        return Guid.TryParse(values.FirstOrDefault(), out var referenceId) ? referenceId : null;
    }

    /// <summary>
    /// Gets the absolute address from the response's <c>Location</c> header, resolved against the request's address if it is relative, or <see
    /// langword="null"/> if the response has no valid header.
    /// </summary>
    private static Uri? GetLocation(HttpResponseMessage response)
    {
        if (response.Headers.Location is not { } location)
            return null;

        if (location.IsAbsoluteUri)
            return location;

        return response.RequestMessage?.RequestUri is { IsAbsoluteUri: true } requestUri ? new Uri(requestUri, location) : null;
    }

    /// <summary>
    /// Gets the time from the response's <c>Retry-After</c> header, which is either a number of seconds or an HTTP date, or <see langword="null"/> if the
    /// response has no valid header.
    /// </summary>
    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta;

        if (retryAfter.Date is not { } date)
            return null;

        // A date counts from the response's own date if it has one, so that a difference between the clocks of the client and the service does not matter.
        var wait = date - (response.Headers.Date ?? DateTimeOffset.UtcNow);
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>
    /// Gets a value indicating whether a response is a sign-in's or a sign-out's, which the service marks with the <c>Session-Change</c> header with the
    /// value <c>sign-in</c> or <c>sign-out</c>, and comes from the host of the base address. A header with another value doesn't mark the response.
    /// </summary>
    internal bool IsSessionChange(HttpRequestMessage request, HttpResponseMessage response) =>
        response.Headers.TryGetValues(SessionHeaders.Change, out var values) && values.All(SessionHeaders.IsChangeValue) &&
        IsBaseAddressHost((response.RequestMessage ?? request).RequestUri);

    /// <summary>
    /// Picks up the session cookie that a response sets, if the response comes from the host of the base address. A response from another host cannot set
    /// or clear the session. The cookie that a sign-in's or sign-out's response sets or deletes (<paramref name="isSessionChange"/>) is taken whatever the
    /// current session token is, and the cookie of any other response only if its request sent the current token (<paramref name="sentToken"/>): such a
    /// response of a request whose session a sign-in replaced while it ran would otherwise end or replace the new session.
    /// </summary>
    internal void UpdateSessionToken(HttpRequestMessage request, HttpResponseMessage response, string? sentToken, bool isSessionChange)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return;

        // The response message's request is the one the response came from, which is a redirected request if a handler followed a redirect.
        if (!IsBaseAddressHost((response.RequestMessage ?? request).RequestUri))
            return;

        string cookieName = SessionCookieName;
        bool found = false;
        string? token = null;
        bool persistent = false;

        // The last session cookie wins, as in browsers.
        foreach (string value in values)
        {
            if (TryParseSessionCookie(value, cookieName, out string? cookieToken, out bool cookiePersistent))
            {
                found = true;
                token = cookieToken;
                persistent = cookiePersistent;
            }
        }

        if (!found)
            return;

        bool ended = false;

        try
        {
            _sessionState.SetFromResponse(isSessionChange, sentToken, token, persistent, ref ended);
        }
        finally
        {
            if (ended)
                _sessionState.Owner.OnSessionEnded();
        }
    }

    /// <summary>
    /// Gets a value indicating whether the address is on the host of the base address, the only host that the session cookie is sent to and taken from.
    /// </summary>
    internal bool IsBaseAddressHost(Uri? address)
    {
        return address is { IsAbsoluteUri: true } && string.Equals(address.Host, GetBaseAddress().Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a <c>Set-Cookie</c> header value if it sets the session cookie. The token is <see langword="null"/> if the cookie is cleared (empty or
    /// expired), and the session is persistent if the cookie has an expiry (<c>Max-Age</c> or <c>Expires</c>).
    /// </summary>
    private static bool TryParseSessionCookie(string setCookie, string cookieName, out string? token, out bool persistent)
    {
        token = null;
        persistent = false;

        var span = setCookie.AsSpan();
        int separatorIndex = span.IndexOf(';');
        var pair = separatorIndex >= 0 ? span[..separatorIndex] : span;
        int eqIndex = pair.IndexOf('=');

        if (eqIndex <= 0 || !pair[..eqIndex].Trim().SequenceEqual(cookieName.AsSpan()))
            return false;

        string value = pair[(eqIndex + 1)..].Trim().ToString();
        long? maxAge = null;
        DateTimeOffset? expires = null;

        var attributes = separatorIndex >= 0 ? span[(separatorIndex + 1)..] : [];

        while (!attributes.IsEmpty)
        {
            int nextIndex = attributes.IndexOf(';');
            var attribute = (nextIndex >= 0 ? attributes[..nextIndex] : attributes).Trim();
            attributes = nextIndex >= 0 ? attributes[(nextIndex + 1)..] : [];

            int attributeEqIndex = attribute.IndexOf('=');

            if (attributeEqIndex <= 0)
                continue;

            var attributeName = attribute[..attributeEqIndex].Trim();
            string attributeValue = attribute[(attributeEqIndex + 1)..].Trim().ToString();

            if (attributeName.Equals("Max-Age".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(attributeValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds))
                    maxAge = seconds;
            }
            else if (attributeName.Equals("Expires".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                if (DateTimeOffset.TryParse(attributeValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    expires = date;
            }
        }

        // Max-Age takes precedence over Expires (RFC 6265).
        bool expired = maxAge is { } age ? age <= 0 : expires <= DateTimeOffset.UtcNow;

        if (value.Length > 0 && !expired)
        {
            token = value;
            persistent = maxAge is not null || expires is not null;
        }

        return true;
    }

    /// <summary>
    /// Builds a full API URL from the base address, path and query string parameters, followed by the user ID precondition if the client has one and the
    /// parameters do not include a parameter of the same name (ignoring case). Including it with a <see langword="null"/> value leaves it out.
    /// </summary>
    /// <param name="path">The path, relative to the base address.</param>
    /// <param name="query">The query string parameters, such as <c>[("page", 2), ("filter", filter)]</c>. Parameters with a <see langword="null"/>
    /// value are left out.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not valid or leads outside the base address.</exception>
    protected Uri GetApiUrl(InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default)
    {
        string pathString = path.ToString();
        var baseAddress = WithTrailingSlash(GetBaseAddress());
        Uri uri;

        try
        {
            uri = new Uri(baseAddress, pathString);
        }
        catch (UriFormatException ex)
        {
            // Such as "a:b", which is read as a drive letter path.
            throw new ArgumentException($"Path '{pathString}' is not a valid relative path or absolute URI.", nameof(path), ex);
        }

        // Covers paths such as "../x", "/x", "//host/x" and absolute URIs of other addresses. A path such as "items:batch" is an absolute URI with the
        // scheme "items".
        if (!baseAddress.IsBaseOf(uri))
            throw new ArgumentException($"Path '{pathString}' {DescribeOutside(baseAddress, uri)} the base address.", nameof(path));

        string? userIdPrecondition = _userIdPrecondition;

        if (userIdPrecondition is not null)
        {
            string preconditionName = UserIdPreconditionQueryName;

            foreach (var (name, _) in query)
            {
                if (string.Equals(name, preconditionName, StringComparison.OrdinalIgnoreCase))
                {
                    userIdPrecondition = null;
                    break;
                }
            }
        }

        if (userIdPrecondition is null && query.Length is 0)
            return uri;

        var builder = new UriBuilder(uri);

        // The query getter includes the leading '?'. It is removed because the setter on .NET Framework always adds one.
        string existingQuery = builder.Query;
        StringBuilder qs = existingQuery.Length > 1 ? new StringBuilder(existingQuery, 1, existingQuery.Length - 1, existingQuery.Length) : new StringBuilder();

        foreach (var (name, value) in query)
            AppendParam(qs, name, value);

        if (userIdPrecondition is not null)
            AppendParam(qs, UserIdPreconditionQueryName, userIdPrecondition);

        builder.Query = qs.ToString();
        return builder.Uri;

        static void AppendParam(StringBuilder qs, string name, object? value)
        {
            if (ApiValueFormatter.Format(value, null) is not string strValue)
                return;

            if (qs.Length > 0)
                qs.Append('&');

            qs.Append(Uri.EscapeDataString(name));
            qs.Append('=');
            qs.Append(Uri.EscapeDataString(strValue));
        }
    }

    /// <summary>
    /// A wait of a hub connection for a cue (see <see cref="SessionState.WaitForCueAsync"/>): the user ID precondition that it waits for, or
    /// <see langword="null"/> for any, and the number of the request that began last before it.
    /// </summary>
    private sealed class CueWait(string? userIdPrecondition, long lastRequestNumber)
    {
        public string? UserIdPrecondition { get; } = userIdPrecondition;

        public long LastRequestNumber { get; } = lastRequestNumber;

        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SessionState
    {
        private readonly Action<string?>? _changedCallback;

#if NET
        private readonly Lock _lock = new();
#else
        private readonly object _lock = new();
#endif

        // Completes when a session starts, and is then replaced for the session after it. It is read without the lock, so that hub connections can read it
        // under their own locks, which the session token changed callback can take under this one.
        private TaskCompletionSource<bool> _nextStart = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The waits of hub connections for the successful response of a request with a user ID precondition (see WaitForCueAsync), under a lock of their
        // own, and the number of the request that began last.
        private readonly List<CueWait> _cueWaits = [];
#if NET
        private readonly Lock _cueLock = new();
#else
        private readonly object _cueLock = new();
#endif
        private long _lastRequestNumber;

        public SessionState(string? token, Action<string?>? changedCallback)
        {
            Token = token;
            IsPersistent = token is not null;
            _changedCallback = changedCallback;
        }

        /// <summary>
        /// Gets or sets the client that created the session state, which the client's children share.
        /// </summary>
        public ApiClientBase Owner { get; set; } = null!;

        public string? Token { get; private set; }

        public bool IsPersistent { get; private set; }

        /// <summary>
        /// Gets a task that completes when a session next starts (see <see cref="Start"/>).
        /// </summary>
        public Task NextStart => Volatile.Read(ref _nextStart).Task;

        /// <summary>
        /// Reports that a session started: completes the task that <see cref="NextStart"/> returned until now. A start that the token reports is reported
        /// after the token is set, so that a connection that sees the start also sees the token.
        /// </summary>
        public void Start()
        {
            var started = Interlocked.Exchange(ref _nextStart, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            started.TrySetResult(true);
        }

        /// <summary>
        /// Gets a value indicating whether a hub connection waits for a cue (see <see cref="WaitForCueAsync"/>).
        /// </summary>
        public bool HasCueWaits
        {
            get {
                lock (_cueLock)
                    return _cueWaits.Count > 0;
            }
        }

        /// <summary>
        /// Records that a request begins, and returns its number, which tells whether it began after a wait for a cue (see <see cref="Cue"/>).
        /// </summary>
        public long BeginRequest() => Interlocked.Increment(ref _lastRequestNumber);

        /// <summary>
        /// Waits until a request that begins after this call gets a successful response with the specified user ID precondition, or with any if it is
        /// <see langword="null"/> (see <see cref="Cue"/>). The wait ends with the token, which cancels the task.
        /// </summary>
        public async Task WaitForCueAsync(string? userIdPrecondition, CancellationToken cancellationToken)
        {
            var wait = new CueWait(userIdPrecondition, Interlocked.Read(ref _lastRequestNumber));

            lock (_cueLock)
                _cueWaits.Add(wait);

            try
            {
                using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), wait.Completion))
                    await wait.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_cueLock)
                    _cueWaits.Remove(wait);
            }
        }

        /// <summary>
        /// Ends the waits for a cue (see <see cref="WaitForCueAsync"/>) that the successful response of the request with the specified number and user ID
        /// precondition matches: those that began before the request and wait for its precondition or for any.
        /// </summary>
        public void Cue(string userIdPrecondition, long requestNumber)
        {
            lock (_cueLock)
            {
                foreach (var wait in _cueWaits)
                {
                    if (requestNumber > wait.LastRequestNumber && (wait.UserIdPrecondition is null || wait.UserIdPrecondition == userIdPrecondition))
                        wait.Completion.TrySetResult(true);
                }
            }
        }

        /// <summary>
        /// Sets the token that a response sets and whether the session is persistent, if the response is a sign-in's or sign-out's
        /// (<paramref name="isSessionChange"/>) or the token is still <paramref name="sentToken"/>, the one that its request sent, and calls the callback
        /// with the token to store, or with <see langword="null"/> if a stored token has to be deleted. <paramref name="ended"/> is set before the callback
        /// is called if the token was cleared. A token that replaces no token, or that a sign-in's response sets, starts a session (see
        /// <see cref="Start"/>): a sign-out's response doesn't set one.
        /// </summary>
        public void SetFromResponse(bool isSessionChange, string? sentToken, string? token, bool persistent, ref bool ended)
        {
            persistent &= token is not null;

            // The callback runs under the lock, so stored tokens change in the same order as the current one.
            lock (_lock)
            {
                if (!isSessionChange && Token != sentToken)
                    return;

                bool wasPersistent = IsPersistent;
                bool tokenChanged = Token != token;
                bool started = token is not null && (Token is null || isSessionChange);

                ended = Token is not null && token is null;
                Token = token;
                IsPersistent = persistent;

                if (started)
                    Start();

                if (persistent)
                {
                    if (tokenChanged || !wasPersistent)
                        _changedCallback?.Invoke(token);
                }
                else if (wasPersistent)
                {
                    _changedCallback?.Invoke(null);
                }
            }
        }

        /// <summary>
        /// Clears the token if it is still the specified one, as a cleared session cookie does: the callback is called with <see langword="null"/> if the
        /// session was persistent. <paramref name="ended"/> is set before the callback is called if the token was cleared.
        /// </summary>
        public void End(string token, ref bool ended)
        {
            lock (_lock)
            {
                if (Token != token)
                    return;

                bool wasPersistent = IsPersistent;

                ended = true;
                Token = null;
                IsPersistent = false;

                if (wasPersistent)
                    _changedCallback?.Invoke(null);
            }
        }

        /// <summary>
        /// Clears the token and calls the callback with <see langword="null"/>, whether or not the session was persistent. <paramref name="ended"/> is set
        /// before the callback is called if there was a token.
        /// </summary>
        public void Clear(ref bool ended)
        {
            lock (_lock)
            {
                ended = Token is not null;
                Token = null;
                IsPersistent = false;
                _changedCallback?.Invoke(null);
            }
        }
    }

    /// <summary>
    /// The handler of the shared HTTP client. It does not follow redirects, because a handler that follows one sends the request's headers, including the
    /// session cookie, to wherever it leads.
    /// </summary>
    private sealed class SharedApiHttpHandler : DelegatingHandler
    {
#if NET
        public SharedApiHttpHandler()
            : base(new SocketsHttpHandler {
                PooledConnectionLifetime = TimeSpan.FromMilliseconds(DefaultHttpClientRefreshDnsTimeout),
                UseCookies = false,
                AllowAutoRedirect = false,
            }) { }
#else
        public SharedApiHttpHandler() : base(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // HttpClientHandler has no pooled connection lifetime setting, so a connection can stay open indefinitely and DNS changes are never picked up.
            // Limiting how long connections to the address are reused has the same effect. The runtime discards idle service points (and their settings),
            // so this is applied on every request instead of once.

            if (request.RequestUri is { IsAbsoluteUri: true } uri)
            {
                var servicePoint = ServicePointManager.FindServicePoint(uri);

                if (servicePoint.ConnectionLeaseTimeout != DefaultHttpClientRefreshDnsTimeout)
                    servicePoint.ConnectionLeaseTimeout = DefaultHttpClientRefreshDnsTimeout;
            }

            return base.SendAsync(request, cancellationToken);
        }
#endif

        // The handler is shared for the life of the process, also as the inner handler of the handlers of hub connections, which dispose their inner
        // handler when they are disposed. It ignores being disposed.
        protected override void Dispose(bool disposing) { }
    }
}
