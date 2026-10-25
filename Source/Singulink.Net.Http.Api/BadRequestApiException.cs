using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when an API request cannot be processed due to a client error (HTTP 400 Bad Request).
/// </summary>
public class BadRequestApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BadRequestApiException"/> class with a specified error message.
    /// </summary>
    public BadRequestApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BadRequestApiException"/> class with a specified error message and inner exception.
    /// </summary>
    public BadRequestApiException(string message, Exception? innerException) : base(HttpStatusCode.BadRequest, message, innerException)
    {
    }
}
