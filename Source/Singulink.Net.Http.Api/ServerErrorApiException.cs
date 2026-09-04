using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an unexpected error that occurred while processing an API request (HTTP 500 Internal Server Error).
/// </summary>
public class ServerErrorApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServerErrorApiException"/> class with a specified error message.
    /// </summary>
    public ServerErrorApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerErrorApiException"/> class with a specified error message and inner exception.
    /// </summary>
    public ServerErrorApiException(string message, Exception? innerException) : base(HttpStatusCode.InternalServerError, message, innerException)
    {
    }
}
