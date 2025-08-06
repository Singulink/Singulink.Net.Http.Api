using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when an API request is forbidden (HTTP 403 Forbidden).
/// </summary>
public class ForbiddenApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ForbiddenApiException"/> class with a specified error message.
    /// </summary>
    public ForbiddenApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ForbiddenApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public ForbiddenApiException(string message, Exception? innerException) : base(HttpStatusCode.Forbidden, message, innerException)
    {
    }
}
