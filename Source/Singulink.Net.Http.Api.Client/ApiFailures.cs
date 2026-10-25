using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Tells apart the failures of API requests and hub connections.
/// </summary>
public static class ApiFailures
{
    /// <summary>
    /// Gets a value indicating whether a request or hub connection failed because the service could not be reached or could not answer for the moment,
    /// so that it may work if it is made again. That is the case for:
    /// <list type="bullet">
    ///   <item>an <see cref="HttpRequestException"/>: the request could not be sent, or its response could not be read in full;</item>
    ///   <item>an <see cref="IOException"/> from a response stream whose connection failed, such as a streaming response that ended before its end record
    ///   (on .NET 10 and later, an <c>HttpIOException</c>);</item>
    ///   <item>a <see cref="WebSocketException"/> from a WebSocket that could not connect or whose connection closed without a close handshake, and a
    ///   <see cref="SocketException"/>;</item>
    ///   <item>an <see cref="OperationCanceledException"/> whose inner exception is a <see cref="TimeoutException"/>: the request timed out;</item>
    ///   <item>a <see cref="ServiceUnavailableApiException"/> (503), such as one with <see cref="ApiErrorCodes.ShortLivedStoreFull"/> or
    ///   <see cref="ApiErrorCodes.CloudflareRangesUnavailable"/>;</item>
    ///   <item>an <see cref="ApiException"/> without an error code for a 502, 504 or 520 to 524 response: a gateway in front of the service, such as a
    ///   reverse proxy or Cloudflare, could not get an answer from it.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Other exceptions are not transient by this measure, including an <see cref="ApiException"/> for a 408 (Request Timeout), which ASP.NET Core sends
    /// for a request body that arrives too slowly, and a <see cref="TooManyRequestsApiException"/> (429), whose <see cref="ApiException.RetryAfter"/> says
    /// how long to wait.
    /// </remarks>
    /// <param name="exception">The exception that the request or hub connection failed with.</param>
    public static bool IsTransient(Exception exception) => exception switch {
        HttpRequestException => true,
#if NET
        HttpIOException => true,
#else
        StreamingResponseEndedException => true,
#endif
        IOException { InnerException: SocketException or WebException } => true,
        WebSocketException { WebSocketErrorCode: WebSocketError.Faulted or WebSocketError.ConnectionClosedPrematurely } => true,
        SocketException => true,
        OperationCanceledException { InnerException: TimeoutException } => true,
        ServiceUnavailableApiException => true,
        ApiException { ErrorCode: null, StatusCode: var status } => (int)status is 502 or 504 or (>= 520 and <= 524),
        _ => false,
    };
}
