using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when a requested resource is not found (HTTP 404 Not Found).
/// </summary>
public class NotFoundApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NotFoundApiException"/> class with a specified error message.
    /// </summary>
    public NotFoundApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NotFoundApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public NotFoundApiException(string message, Exception? innerException) : base(HttpStatusCode.NotFound, message, innerException)
    {
    }
}
