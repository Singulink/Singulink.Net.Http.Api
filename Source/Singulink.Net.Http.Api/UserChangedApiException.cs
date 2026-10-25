using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs when the user ID precondition of an API request does not match the user of the request's session (HTTP 412
/// Precondition Failed).
/// </summary>
public class UserChangedApiException : ApiException
{
    /// <summary>
    /// Gets the ID of the user that the request or hub connection was made for: its user ID precondition, or <see langword="null"/> if it is not known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Errors that the service sends don't carry it, nor the user of the session that the service found. The API client sets it from the precondition that
    /// it sent: for the error response or streaming response error record of a request, and for the errors of a hub connection that an API client
    /// created, including a refusal of the connection. It is <see langword="null"/> for a request or hub connection without a precondition, and for a hub
    /// connection that wraps a <c>HubConnection</c> of the app's own.
    /// </para>
    /// <para>
    /// On the service, the toolkit's session contexts set it to the precondition of the request that they refuse.
    /// </para>
    /// </remarks>
    public string? ExpectedUserId { get; init; }

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
