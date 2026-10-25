using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when validation fails for an API request (HTTP 422 Unprocessable Content).
/// </summary>
public class ValidationApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationApiException"/> class with a specified error message.
    /// </summary>
    public ValidationApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public ValidationApiException(string message, Exception? innerException) : base((HttpStatusCode)422, message, innerException)
    {
    }
}
