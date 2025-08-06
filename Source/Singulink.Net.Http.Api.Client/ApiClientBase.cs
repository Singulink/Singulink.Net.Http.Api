using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Base class for API clients.
/// </summary>
/// <param name="httpClientFactory">Optional HTTP client factory to create HTTP clients.</param>
public abstract class ApiClientBase(IHttpClientFactory? httpClientFactory = null)
{
    private readonly struct VoidResponse;

    private const int DefaultHttpClientRefreshDnsTimeout = 60 * 1000;

    private static readonly Lazy<HttpClient> _defaultHttpClient = new(() => {
        if (OperatingSystem.IsBrowser())
            return new HttpClient();

        return new HttpClient(new SocketsHttpHandler {
            PooledConnectionLifetime = TimeSpan.FromMilliseconds(DefaultHttpClientRefreshDnsTimeout),
        });
    });

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
    /// Invoked when an API request is being sent. Can be overridden to customize the request before sending it (e.g., adding headers).
    /// </summary>
    protected virtual void OnRequestSending(ApiRequest request) { }

    /// <summary>
    /// Gets the default base address for the API client that is used if HTTP client does not have a base address set.
    /// </summary>
    protected abstract Uri GetDefaultBaseAddress();

    /// <summary>
    /// Sends an API request with no response content expected.
    /// </summary>
    protected Task SendAsync(ApiRequest request) => SendAsync<VoidResponse>(request);

    /// <summary>
    /// Sends an API request and expects a response of type <typeparamref name="T"/>. T is JSON deserialized if it is not one of the following types: <see
    /// cref="HttpResponseMessage"/>, <see cref="string"/>, <see cref="Stream"/>.
    /// </summary>
    /// <exception cref="HttpRequestException">An error occurred while sending the request.</exception>
    /// <exception cref="NotFoundApiException">A 404 (Not Found) response was returned.</exception>
    /// <exception cref="UnauthorizedApiException">A 401 (Unauthorized) response was returned.</exception>
    /// <exception cref="ForbiddenApiException">A 403 (Forbidden) response was returned.</exception>
    /// <exception cref="ValidationApiException">A 400 (Bad Request) response was returned.</exception>
    /// <exception cref="UserChangedApiException">A 440 (User Changed) response was returned.</exception>
    /// <exception cref="ApiException">A response other than 200 (OK) or one of the other known/expected error codes was returned.</exception>
    protected async Task<T> SendAsync<T>(ApiRequest request)
    {
        OnRequestSending(request);
        HttpResponseMessage response;

        try
        {
            response = await request.Client.SendAsync(request.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new HttpRequestException("Connection problem - please try again.", ex);
        }

        try
        {
            if (response.StatusCode is HttpStatusCode.OK)
            {
                if (typeof(T) == typeof(VoidResponse))
                    return default!;

                if (typeof(T) == typeof(HttpResponseMessage))
                    return (T)(object)response;

                if (typeof(T) == typeof(string))
                    return (T)(object)await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (typeof(T) == typeof(Stream))
                    return (T)(object)await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

                return await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false) ?? throw new FormatException("Empty response.");
            }

            string errorMessage = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(errorMessage))
                errorMessage = $"Unknown service error ({response.StatusCode}) - please try again later or update your app if there is an update available.";

            if (response.StatusCode is HttpStatusCode.NotFound)
                throw new NotFoundApiException(errorMessage);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
                throw new UnauthorizedApiException(errorMessage);

            if (response.StatusCode is HttpStatusCode.Forbidden)
                throw new ForbiddenApiException(errorMessage);

            if (response.StatusCode is HttpStatusCode.BadRequest)
                throw new ValidationApiException(errorMessage);

            if ((int)response.StatusCode is 440)
                throw new UserChangedApiException(errorMessage);

            throw new ApiException(response.StatusCode, errorMessage);
        }
        finally
        {
            request.Message.Dispose();
            response.Dispose();
        }
    }

    private HttpClient GetHttpClient() => httpClientFactory?.CreateClient() ?? _defaultHttpClient.Value;

    private Uri GetApiUrl(HttpClient client, string path, ReadOnlySpan<(string Name, object Value)> queryStringParams)
    {
        var uri = new Uri(client.BaseAddress ?? GetDefaultBaseAddress(), path);

        if (queryStringParams.Length is 0)
            return uri;

        var qs = new StringBuilder();

        bool first = true;

        foreach (var (name, value) in queryStringParams)
        {
            if (value is null || GetValueString(value) is not string strValue)
                continue;

            if (!first)
                qs.Append('&');

            qs.Append(name);
            qs.Append('=');
            qs.Append(Uri.EscapeDataString(strValue));

            first = false;
        }

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
