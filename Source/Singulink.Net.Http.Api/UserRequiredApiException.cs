using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when an API request user ID precondition header is required but missing (HTTP 428 Precondition Required).
/// </summary>
public class UserRequiredApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UserRequiredApiException"/> class with a specified error message.
    /// </summary>
    public UserRequiredApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UserRequiredApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public UserRequiredApiException(string message, Exception? innerException) : base(HttpStatusCode.PreconditionRequired, message, innerException)
    {
    }
}
