using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when the client has sent too many API requests (HTTP 429 Too Many Requests).
/// </summary>
public class TooManyRequestsApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TooManyRequestsApiException"/> class with a specified error message.
    /// </summary>
    public TooManyRequestsApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TooManyRequestsApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public TooManyRequestsApiException(string message, Exception? innerException) : base((HttpStatusCode)429, message, innerException)
    {
    }
}
