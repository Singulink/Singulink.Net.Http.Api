using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when validation fails for an API request (HTTP 400 Bad Request).
/// </summary>
public class ValidationApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationApiException"/> class with a specified error message.
    /// </summary>
    public ValidationApiException(string message) : base(HttpStatusCode.BadRequest, message)
    {
    }
}
