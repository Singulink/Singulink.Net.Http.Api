namespace Singulink.Net.Http.Api;

/// <summary>
/// Represents the content of an API error response, including the content as a string and its content type. Used to store error details when the error response
/// is not in <c>text/plain</c> or a known/expected format.
/// </summary>
public class ApiErrorContent
{
    /// <summary>
    /// Gets the content of the API error response as a string.
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// Gets the content type of the API error response.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiErrorContent"/> class with the specified content and content type.
    /// </summary>
    public ApiErrorContent(string content, string contentType)
    {
        Content = content;
        ContentType = contentType;
    }
}
