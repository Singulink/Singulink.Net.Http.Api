using System.Net.Http.Json;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Represents an API request that can be sent from an <see cref="ApiClientBase"/> implementation.
/// </summary>
public readonly record struct ApiRequest
{
    internal HttpClient Client { get; }

    /// <summary>
    /// Gets the HTTP request message that will be sent to the API.
    /// </summary>
    public HttpRequestMessage Message { get; }

    internal ApiRequest(HttpClient client, HttpMethod method, Uri url)
    {
        Client = client;
        Message = new HttpRequestMessage(method, url);
    }

    /// <summary>
    /// Sets the content of the request message to the specified object.
    /// </summary>
    /// <param name="contentObj">The object to serialize to JSON and set as the request content. If null, the content will be cleared.</param>
    public void SetJsonContent(object? contentObj)
    {
        if (contentObj is not null)
            Message.Content = JsonContent.Create(contentObj);
        else
            Message.Content = null;
    }
}
