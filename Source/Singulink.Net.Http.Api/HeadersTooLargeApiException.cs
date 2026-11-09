using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when a header of an API request, or its headers together, are larger than the service accepts (HTTP 431 Request
/// Header Fields Too Large).
/// </summary>
public class HeadersTooLargeApiException : ApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HeadersTooLargeApiException"/> class with a specified error message.
    /// </summary>
    public HeadersTooLargeApiException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HeadersTooLargeApiException"/> class with a specified error message and an inner exception.
    /// </summary>
    public HeadersTooLargeApiException(string message, Exception? innerException) : base((HttpStatusCode)431, message, innerException)
    {
    }
}
