using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs during API operations.
/// </summary>
public class ApiException : Exception
{
    /// <summary>
    /// Gets the HTTP status code associated with the API exception.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets the error content associated with the API exception if the error response was not in <c>text/plain</c> or a known/expected format.
    /// </summary>
    public ApiErrorContent? ErrorContent { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiException"/> class with a specified HTTP status code and error message.
    /// </summary>
    public ApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiException"/> class with a specified HTTP status code, error message and inner exception.
    /// </summary>
    public ApiException(HttpStatusCode statusCode, string message, Exception? innerException) : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
