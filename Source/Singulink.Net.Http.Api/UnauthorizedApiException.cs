using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when the user is unauthorized (HTTP 401 Unauthorized).
/// </summary>
public class UnauthorizedApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnauthorizedApiException"/> class with a specified error message.
    /// </summary>
    public UnauthorizedApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnauthorizedApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public UnauthorizedApiException(string message, Exception? innerException) : base(HttpStatusCode.Unauthorized, message, innerException)
    {
    }
}
