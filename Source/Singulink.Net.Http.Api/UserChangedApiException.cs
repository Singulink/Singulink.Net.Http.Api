using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when an API request user ID precondition does not match the current user (HTTP 412 Precondition Failed).
/// </summary>
public class UserChangedApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UserChangedApiException"/> class with a specified error message.
    /// </summary>
    public UserChangedApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UserChangedApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public UserChangedApiException(string message, Exception? innerException) : base(HttpStatusCode.PreconditionFailed, message, innerException)
    {
    }
}
