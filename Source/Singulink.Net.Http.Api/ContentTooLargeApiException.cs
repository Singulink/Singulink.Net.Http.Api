using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when the content of an API request is larger than the service accepts (HTTP 413 Content Too Large).
/// </summary>
public class ContentTooLargeApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContentTooLargeApiException"/> class with a specified error message.
    /// </summary>
    public ContentTooLargeApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentTooLargeApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public ContentTooLargeApiException(string message, Exception? innerException) : base(HttpStatusCode.RequestEntityTooLarge, message, innerException)
    {
    }
}
