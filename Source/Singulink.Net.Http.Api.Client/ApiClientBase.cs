using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Base class for API clients.
/// </summary>
public abstract class ApiClientBase
{
    /// <summary>
    /// The default query parameter name for the user ID precondition that is used to ensure the cookie session user ID matches the expected user ID
    /// making the API request. Value is <c>"if-userId"</c>.
    /// </summary>
    /// <remarks>
    /// The user ID precondition can be included automatically by overriding <see cref="GetDefaultQueryParams(string)"/> to yield a tuple with this key and the
    /// user ID value, or it can be passed explicitly in per-call query string parameters.
    /// </remarks>
    protected const string DefaultUserIdPreconditionQueryName = "if-userId";

    /// <summary>
    /// The default name of the cookie that holds the encrypted session token. Value is <c>"session-token"</c>.
    /// </summary>
    protected const string DefaultSessionCookieName = "session-token";

    private const int DefaultHttpClientRefreshDnsTimeout = 60 * 1000;

    private static string? _defaultUserAgent;

    private readonly JsonSerializerOptions _defaultSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ApiClientBase? _parentClient;

    private static readonly Lazy<HttpClient> _defaultHttpClient = new(() => {
        if (OperatingSystem.IsBrowser())
            return new HttpClient();

        return new HttpClient(new SocketsHttpHandler {
            PooledConnectionLifetime = TimeSpan.FromMilliseconds(DefaultHttpClientRefreshDnsTimeout),
            UseCookies = false,
        });
    });

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly SessionState _sessionState;

    /// <summary>
    /// Gets the JSON serializer options used for serializing and deserializing API request and response content. Defaults to options provided by <see
    /// cref="JsonSerializerDefaults.Web"/>.
    /// </summary>
    protected virtual JsonSerializerOptions SerializerOptions => _parentClient?.SerializerOptions ?? _defaultSerializerOptions;

    /// <summary>
    /// Gets the name of the cookie that holds the encrypted session token. Defaults to <see cref="DefaultSessionCookieName"/>.
    /// </summary>
    protected virtual string SessionCookieName => DefaultSessionCookieName;

    /// <summary>
    /// Gets the user agent string sent with requests. On non-browser platforms, this is automatically applied to HTTP requests. If a parent client
    /// exists, returns the parent's value by default.
    /// </summary>
    protected virtual string UserAgent
    {
        get {
            return _parentClient?.UserAgent ?? (_defaultUserAgent ??= GetDefaultUserAgent());

            static string GetDefaultUserAgent()
            {
                var assemblyName = typeof(ApiClientBase).Assembly.GetName();
                string version = assemblyName.Version?.ToString() ?? "1.0";
                return $"{assemblyName.Name}/{version}";
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the session token should be persisted via the session token changed callback.
    /// </summary>
    protected bool IsPersistentSession
    {
        get => _sessionState.IsPersistentSession;
        set
        {
            if (_sessionState.IsPersistentSession != value)
            {
                _sessionState.IsPersistentSession = value;
                _sessionState.ChangedCallback?.Invoke(value ? _sessionState.Token : null);
            }
        }
    }

    /// <summary>
    /// Gets the current session token.
    /// </summary>
    protected string? SessionToken
    {
        get => _sessionState.Token;
        private set
        {
            if (_sessionState.Token != value)
            {
                _sessionState.Token = value;

                if (_sessionState.IsPersistentSession)
                    _sessionState.ChangedCallback?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class with an optional HTTP client factory.
    /// </summary>
    protected ApiClientBase(IHttpClientFactory? httpClientFactory = null) : this(httpClientFactory, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class with an optional HTTP client factory, session token and session token changed callback.
    /// </summary>
    protected ApiClientBase(IHttpClientFactory? httpClientFactory, string? sessionToken, Action<string?>? sessionTokenChanged)
    {
        _httpClientFactory = httpClientFactory;
        _sessionState = new SessionState {
            Token = sessionToken,
            ChangedCallback = sessionTokenChanged,
            IsPersistentSession = sessionToken is not null,
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class that shares the session state with the specified parent client.
    /// </summary>
    protected ApiClientBase(ApiClientBase parent)
    {
        _httpClientFactory = parent._httpClientFactory;
        _sessionState = parent._sessionState;
        _parentClient = parent;
    }

    /// <summary>
    /// Gets the default query string parameters to include in all API requests and hub connections. Per-call parameters with the same key override
    /// default parameters. If a parent client exists, returns the parent's defaults. Override this method to inject common parameters such as the user
    /// ID precondition.
    /// </summary>
    /// <param name="path">The request path, which can be used to conditionally include or exclude default parameters.</param>
    /// <returns>A span of default query string parameters. Returns an empty span if there are no defaults.</returns>
    protected virtual ReadOnlySpan<(string Name, object? Value)> GetDefaultQueryParams(string path)
    {
        if (_parentClient is not null)
            return _parentClient.GetDefaultQueryParams(path);

        return [];
    }

    /// <summary>
    /// Creates an API request with the specified HTTP method, path, and optional query string parameters.
    /// </summary>
    /// <param name="method">The HTTP method for the request (e.g., GET, POST).</param>
    /// <param name="path">The API path to which the request will be sent.</param>
    /// <param name="queryStringParams">Optional query string parameters to include in the request.</param>
    protected virtual HttpRequestMessage CreateRequest(HttpMethod method, string path, params ReadOnlySpan<(string Name, object? Value)> queryStringParams)
    {
        var url = GetApiUrl(path, queryStringParams);
        return new HttpRequestMessage(method, url);
    }

    /// <summary>
    /// Gets the default base address for the API client that is used if HTTP client does not have a base address set.
    /// </summary>
    protected abstract Uri GetBaseAddress();

    /// <summary>
    /// Sends an API request.
    /// </summary>
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)" path="/exception"/>
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
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)" path="/exception"/>
    protected async Task<TResponse> SendAsync<TResponse>(HttpRequestMessage request, CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        return await SendAsync<TResponse>(request, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an API request with the specified content and returns the response.
    /// </summary>
    /// <exception cref="HttpRequestException">An error occurred while sending the request.</exception>
    /// <exception cref="BadRequestApiException">A 400 (Bad Request) response was returned.</exception>
    /// <exception cref="UnauthorizedApiException">A 401 (Unauthorized) response was returned.</exception>
    /// <exception cref="ForbiddenApiException">A 403 (Forbidden) response was returned.</exception>
    /// <exception cref="NotFoundApiException">A 404 (Not Found) response was returned.</exception>
    /// <exception cref="UserChangedApiException">A 412 (Precondition Failed) response was returned.</exception>
    /// <exception cref="ValidationApiException">A 422 (Unprocessable Content/Entity) response was returned.</exception>
    /// <exception cref="UserRequiredApiException">A 428 (Precondition Required) response was returned.</exception>
    /// <exception cref="ApiException">A response other than 2xx or one of the other known/expected error codes was returned.</exception>
    protected virtual async Task<TResponse> SendAsync<TResponse>(HttpRequestMessage request, object? content, CancellationToken cancellationToken = default)
        where TResponse : notnull
    {
        HttpResponseMessage response;

        try
        {
            PrepareRequest(request, content);

            var completionOption = typeof(TResponse) == typeof(VoidApiResponse)
                ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead;

            response = await GetHttpClient().SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }

        if (!OperatingSystem.IsBrowser())
            UpdateSessionToken(response);

        try
        {
            await ThrowOnErrorResponse(response, cancellationToken).ConfigureAwait(false);

            if (typeof(TResponse) == typeof(VoidApiResponse))
                return default!;

            if (typeof(TResponse) == typeof(HttpResponseMessage))
                return (TResponse)(object)response;

            if (typeof(TResponse) == typeof(string))
                return (TResponse)(object)await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return await response.Content.ReadFromJsonAsync<TResponse>(SerializerOptions, cancellationToken).ConfigureAwait(false) ?? throw new FormatException("Empty response.");
        }
        finally
        {
            if (typeof(TResponse) != typeof(HttpResponseMessage))
                response.Dispose();
        }
    }

    /// <summary>
    /// Sends an API request and returns a streaming response.
    /// </summary>
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)" path="/exception"/>
    protected IAsyncEnumerable<TItem> SendStreamingAsync<TItem>(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        return SendStreamingAsync<TItem>(request, null, cancellationToken);
    }

    /// <summary>
    /// Sends an API request with the specified content and returns a streaming response.
    /// </summary>
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)" path="/exception"/>
    protected virtual async IAsyncEnumerable<TItem> SendStreamingAsync<TItem>(
        HttpRequestMessage request, object? content, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;

        try
        {
            PrepareRequest(request, content);
            response = await GetHttpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }

        if (!OperatingSystem.IsBrowser())
            UpdateSessionToken(response);

        try
        {
            await ThrowOnErrorResponse(response, cancellationToken).ConfigureAwait(false);

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<TItem>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false))
                {
                    if (item is not null)
                        yield return item;
                }
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Prepares the request by setting content and session cookie header.
    /// </summary>
    private void PrepareRequest(HttpRequestMessage request, object? content)
    {
        if (content is not null)
        {
            if (request.Content is not null)
                throw new ArgumentException("Request content has already been set.");

            if (content is HttpContent httpContent)
                request.Content = httpContent;
            else if (content is string strContent)
                request.Content = new StringContent(strContent);
            else
                request.Content = JsonContent.Create(content, options: SerializerOptions);
        }

        if (!OperatingSystem.IsBrowser())
        {
            if (_sessionState.Token is not null)
                request.Headers.Add("Cookie", $"{SessionCookieName}={_sessionState.Token}");

            request.Headers.UserAgent.ParseAdd(UserAgent);
        }
    }

    /// <summary>
    /// Throws an appropriate API exception if the response indicates an error.
    /// </summary>
    private async ValueTask ThrowOnErrorResponse(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is >= HttpStatusCode.OK and <= (HttpStatusCode)299)
            return;

        string? errorContentType = response.Content.Headers.ContentType?.MediaType;
        string errorContentString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        string? errorMessage = null;
        ApiErrorContent? errorContent = null;

        if (errorContentType is "text/plain")
            errorMessage = errorContentString;
        else if (!string.IsNullOrWhiteSpace(errorContentString))
            errorContent = new(errorContentString, errorContentType ?? "unknown");

        if (string.IsNullOrWhiteSpace(errorMessage))
            errorMessage = $"Unknown service error ({response.StatusCode}).";

        var ex = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => new BadRequestApiException(errorMessage) { ErrorContent = errorContent },
            HttpStatusCode.Unauthorized => new UnauthorizedApiException(errorMessage) { ErrorContent = errorContent },
            HttpStatusCode.Forbidden => new ForbiddenApiException(errorMessage) { ErrorContent = errorContent },
            HttpStatusCode.NotFound => new NotFoundApiException(errorMessage) { ErrorContent = errorContent },
            HttpStatusCode.PreconditionFailed => new UserChangedApiException(errorMessage) { ErrorContent = errorContent },
            HttpStatusCode.UnprocessableEntity => new ValidationApiException(errorMessage) { ErrorContent = errorContent },
            HttpStatusCode.PreconditionRequired => new UserRequiredApiException(errorMessage) { ErrorContent = errorContent },
            _ => new ApiException(response.StatusCode, errorMessage) { ErrorContent = errorContent },
        };

        throw ex;
    }

    private void UpdateSessionToken(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (string value in values)
            {
                var span = value.AsSpan();
                int separatorIndex = span.IndexOf(';');

                if (separatorIndex >= 0)
                    span = span[..separatorIndex];

                int eqIndex = span.IndexOf('=');

                if (eqIndex > 0)
                {
                    var name = span[..eqIndex].Trim();

                    if (name.SequenceEqual(SessionCookieName))
                    {
                        string val = span[(eqIndex + 1)..].Trim().ToString();
                        SessionToken = string.IsNullOrEmpty(val) ? null : val;
                        return;
                    }
                }
            }
        }
    }

    private HttpClient GetHttpClient() => _httpClientFactory?.CreateClient() ?? _defaultHttpClient.Value;

    /// <summary>
    /// Builds a full API URL from the base address, path, and query string parameters. Default query parameters from <see cref="GetDefaultQueryParams"/>
    /// are automatically merged with per-call parameters (per-call parameters with the same key take precedence).
    /// </summary>
    protected Uri GetApiUrl(string path, ReadOnlySpan<(string Name, object? Value)> queryStringParams)
    {
        var uri = new Uri(GetBaseAddress(), path);
        ReadOnlySpan<(string Name, object? Value)> defaults = GetDefaultQueryParams(path);

        if (defaults.Length is 0 && queryStringParams.Length is 0)
            return uri;

        var builder = new UriBuilder(uri);
        string query = builder.Query;
        StringBuilder qs = query.Length > 1 ? new StringBuilder(query) : new StringBuilder();

        // Add call params
        foreach (var (name, value) in queryStringParams)
            AppendParam(qs, name, value);

        // Add defaults that are not overridden by call params
        foreach (var (name, value) in defaults)
        {
            bool overridden = false;

            foreach (var (callName, _) in queryStringParams)
            {
                if (string.Equals(name, callName, StringComparison.OrdinalIgnoreCase))
                {
                    overridden = true;
                    break;
                }
            }

            if (!overridden)
                AppendParam(qs, name, value);
        }

        builder.Query = qs.ToString();
        return builder.Uri;

        static void AppendParam(StringBuilder qs, string name, object? value)
        {
            if (GetValueString(value) is not string strValue)
                return;

            if (qs.Length > 0)
                qs.Append('&');

            qs.Append(Uri.EscapeDataString(name));
            qs.Append('=');
            qs.Append(Uri.EscapeDataString(strValue));
        }

        static string? GetValueString(object? value)
        {
            if (value is null)
                return null;

            if (value is byte[] bytes)
                return Convert.ToBase64String(bytes);

            if (value is IFormattable formattable)
            {
                string format = null;

                if (value is DateTime or DateTimeOffset or DateOnly or TimeOnly)
                    format = "O";

                return formattable.ToString(format, CultureInfo.InvariantCulture);
            }

            return value.ToString();
        }
    }

    private sealed class SessionState
    {
        public string? Token { get; set; }

        public bool IsPersistentSession { get; set; }

        public Action<string?>? ChangedCallback { get; set; }
    }
}
