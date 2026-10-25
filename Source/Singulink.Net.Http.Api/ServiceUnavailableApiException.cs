using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when the service cannot handle API requests at the moment (HTTP 503 Service Unavailable).
/// </summary>
public class ServiceUnavailableApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceUnavailableApiException"/> class with a specified error message.
    /// </summary>
    public ServiceUnavailableApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceUnavailableApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public ServiceUnavailableApiException(string message, Exception? innerException) : base(HttpStatusCode.ServiceUnavailable, message, innerException)
    {
    }
}
