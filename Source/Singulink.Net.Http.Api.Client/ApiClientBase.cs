using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Base class for API clients.
/// </summary>
public abstract class ApiClientBase
{
    private readonly JsonSerializerOptions _defaultSerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The default key for the user ID precondition header that is used to ensure the cookie session user ID matches the expected user ID making the API
    /// request. Value is <c>"If-User-Id"</c>.
    /// </summary>
    /// <remarks>
    /// If the API is making authorized requests then this header should be set to the user ID of the authenticated user by the API client implementation in the
    /// <see cref="SendAsync{TResponse}(HttpRequestMessage, object?, CancellationToken)"/> method before calling the base implementation. It matches the default
    /// key expected by <c>CookieSessionHandler</c>.
    /// </remarks>
    protected const string DefaultUserIdPreconditionHeaderName = "If-User-ID";

    /// <summary>
    /// The default name of the cookie that holds the encrypted session token. Value is <c>"session-token"</c>.
    /// </summary>
    protected const string DefaultSessionCookieName = "session-token";

    private const int DefaultHttpClientRefreshDnsTimeout = 60 * 1000;

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
    protected virtual JsonSerializerOptions SerializerOptions { get; }

    /// <summary>
    /// Gets the name of the cookie that holds the encrypted session token. Defaults to <see cref="DefaultSessionCookieName"/>.
    /// </summary>
    protected virtual string SessionCookieName => DefaultSessionCookieName;

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
        SerializerOptions = _defaultSerializerOptions;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class that shares the session state with the specified parent client.
    /// </summary>
    protected ApiClientBase(ApiClientBase parent)
    {
        _httpClientFactory = parent._httpClientFactory;
        _sessionState = parent._sessionState;
        SerializerOptions = parent.SerializerOptions;
    }

    /// <summary>
    /// Creates an API request with the specified HTTP method, path, and optional query string parameters.
    /// </summary>
    /// <param name="method">The HTTP method for the request (e.g., GET, POST).</param>
    /// <param name="path">The API path to which the request will be sent.</param>
    /// <param name="queryStringParams">Optional query string parameters to include in the request.</param>
    protected HttpRequestMessage CreateRequest(HttpMethod method, string path, params ReadOnlySpan<(string Name, object? Value)> queryStringParams)
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
            if (content is not null)
            {
                if (request.Content is not null)
                {
                    throw new ArgumentException("Request content has already been set.");
                }
                else if (content is HttpContent httpContent)
                {
                    request.Content = httpContent;
                }
                else if (content is string strContent)
                {
                    request.Content = new StringContent(strContent);
                }
                else
                {
                    request.Content = JsonContent.Create(content, options: SerializerOptions);
                }
            }

            if (!OperatingSystem.IsBrowser() && _sessionState.Token is not null)
                request.Headers.Add("Cookie", $"{SessionCookieName}={_sessionState.Token}");

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
            if (response.StatusCode is >= HttpStatusCode.OK and <= (HttpStatusCode)299)
            {
                if (typeof(TResponse) == typeof(VoidApiResponse))
                    return default!;

                if (typeof(TResponse) == typeof(HttpResponseMessage))
                    return (TResponse)(object)response;

                if (typeof(TResponse) == typeof(string))
                    return (TResponse)(object)await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken).ConfigureAwait(false) ?? throw new FormatException("Empty response.");
            }

            string errorContentType = response.Content.Headers.ContentType?.MediaType;
            string errorContentString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string errorMessage = null;
            ApiErrorContent errorContent = null;

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
        finally
        {
            if (typeof(TResponse) != typeof(HttpResponseMessage))
                response.Dispose();
        }
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

    private Uri GetApiUrl(string path, ReadOnlySpan<(string Name, object? Value)> queryStringParams)
    {
        var uri = new Uri(GetBaseAddress(), path);

        if (queryStringParams.Length is 0)
            return uri;

        var builder = new UriBuilder(uri);
        string query = builder.Query;
        StringBuilder qs;

        if (query.Length > 1)
            qs = new StringBuilder(query);
        else
            qs = new StringBuilder();

        foreach (var (name, value) in queryStringParams)
        {
            if (value is null || GetValueString(value) is not string strValue)
                continue;

            if (qs.Length > 0)
                qs.Append('&');

            qs.Append(Uri.EscapeDataString(name));
            qs.Append('=');
            qs.Append(Uri.EscapeDataString(strValue));
        }

        builder.Query = qs.ToString();
        return builder.Uri;

        static string? GetValueString(object value)
        {
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
