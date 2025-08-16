using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Base class for API clients.
/// </summary>
public abstract class ApiClientBase
{
    /// <summary>
    /// The default key for the user ID precondition header that is used to ensure the cookie session user ID matches the expected user ID making the API
    /// request. Value is <c>"If-UserId"</c>.
    /// </summary>
    /// <remarks>
    /// If the API is making authenticated requests then this header should be set to the user ID of the authenticated user by the API client implementation in
    /// the <see cref="SendAsync{T}(ApiRequest, CancellationToken)"/> method before calling the base implementation. It matches the default key used by
    /// <c>CookieSessionHandler</c> implementations.
    /// </remarks>
    protected const string UserIdPreconditionHeaderKey = "If-UserId";

    private const int DefaultHttpClientRefreshDnsTimeout = 60 * 1000;

    private static readonly Lazy<HttpClient> _defaultHttpClient = new(() => {
        if (OperatingSystem.IsBrowser())
            return new HttpClient();

        return new HttpClient(new SocketsHttpHandler {
            PooledConnectionLifetime = TimeSpan.FromMilliseconds(DefaultHttpClientRefreshDnsTimeout),
        });
    });

    private readonly IHttpClientFactory? _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClientBase"/> class with an optional HTTP client factory.
    /// </summary>
    protected ApiClientBase(IHttpClientFactory? httpClientFactory = null)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Creates an API request with the specified HTTP method, path, and optional query string parameters.
    /// </summary>
    /// <param name="method">The HTTP method for the request (e.g., GET, POST).</param>
    /// <param name="path">The API path to which the request will be sent.</param>
    /// <param name="queryStringParams">Optional query string parameters to include in the request.</param>
    protected ApiRequest CreateRequest(HttpMethod method, string path, params ReadOnlySpan<(string Name, object Value)> queryStringParams)
    {
        var client = GetHttpClient();
        var url = GetApiUrl(client, path, queryStringParams);
        return new ApiRequest(client, method, url);
    }

    /// <summary>
    /// Gets the default base address for the API client that is used if HTTP client does not have a base address set.
    /// </summary>
    protected abstract Uri GetDefaultBaseAddress();

    /// <summary>
    /// Sends an API request with no response content expected.
    /// </summary>
    protected Task SendAsync(ApiRequest request, CancellationToken cancellationToken = default) => SendAsync<VoidApiResponse>(request, cancellationToken);

    /// <summary>
    /// Sends an API request and expects a response of type <typeparamref name="T"/>. <typeparamref name="T"/> is JSON deserialized if it is not one of the
    /// following types: <see cref="HttpResponseMessage"/>, <see cref="string"/>, <see cref="Stream"/>.
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
    protected virtual async Task<T> SendAsync<T>(ApiRequest request, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;

        try
        {
            var completionOption = typeof(T) == typeof(VoidApiResponse)
                ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead;

            response = await request.Client.SendAsync(request.Message, completionOption, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }

        try
        {
            if (response.StatusCode is >= HttpStatusCode.OK and <= (HttpStatusCode)299)
            {
                if (typeof(T) == typeof(VoidApiResponse))
                    return default!;

                if (typeof(T) == typeof(HttpResponseMessage))
                    return (T)(object)response;

                if (typeof(T) == typeof(string))
                    return (T)(object)await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (typeof(T) == typeof(Stream))
                    return (T)(object)await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false) ?? throw new FormatException("Empty response.");
            }

            string errorContentType = response.Content.Headers.ContentType?.MediaType;
            string errorContentString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string errorMessage = null;
            ApiErrorContent errorContent = null;

            if (errorContentType is "text/plain")
            {
                errorMessage = errorContentString;
            }
            else if (!string.IsNullOrWhiteSpace(errorContentString))
            {
                errorContent = new(errorContentType ?? "unknown", errorContentString);
            }

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
            response.Dispose();
        }
    }

    private HttpClient GetHttpClient() => _httpClientFactory?.CreateClient() ?? _defaultHttpClient.Value;

    private Uri GetApiUrl(HttpClient client, string path, ReadOnlySpan<(string Name, object Value)> queryStringParams)
    {
        var uri = new Uri(client.BaseAddress ?? GetDefaultBaseAddress(), path);

        if (queryStringParams.Length is 0)
            return uri;

        StringBuilder qs = null;

        foreach (var (name, value) in queryStringParams)
        {
            if (value is null || GetValueString(value) is not string strValue)
                continue;

            if (qs is null)
                qs = new();
            else
                qs.Append('&');

            qs.Append(name);
            qs.Append('=');
            qs.Append(Uri.EscapeDataString(strValue));
        }

        if (qs is null)
            return uri;

        return new UriBuilder(uri) { Query = qs.ToString() }.Uri;

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
}
