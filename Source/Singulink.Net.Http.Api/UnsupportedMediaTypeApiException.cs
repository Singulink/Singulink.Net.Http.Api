using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when the content type of an API request is not supported (HTTP 415 Unsupported Media Type).
/// </summary>
public class UnsupportedMediaTypeApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedMediaTypeApiException"/> class with a specified error message.
    /// </summary>
    public UnsupportedMediaTypeApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedMediaTypeApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public UnsupportedMediaTypeApiException(string message, Exception? innerException) : base(HttpStatusCode.UnsupportedMediaType, message, innerException)
    {
    }
}
