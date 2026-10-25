using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when the API request conflicts with the current state of the target resource (HTTP 409 Conflict).
/// </summary>
public class ConflictApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConflictApiException"/> class with a specified error message.
    /// </summary>
    public ConflictApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConflictApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public ConflictApiException(string message, Exception? innerException) : base(HttpStatusCode.Conflict, message, innerException)
    {
    }
}
