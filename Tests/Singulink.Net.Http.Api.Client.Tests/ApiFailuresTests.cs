using System.Net.Sockets;
using System.Net.WebSockets;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class ApiFailuresTests
{
    public static IEnumerable<object[]> TransientFailures => [
        [new HttpRequestException("The connection was refused.")],
        [new IOException("The connection was reset.", new SocketException())],
        [new WebSocketException(WebSocketError.Faulted, "Unable to connect to the remote server")],
        [new WebSocketException(WebSocketError.ConnectionClosedPrematurely)],
        [new SocketException()],
        [new TaskCanceledException("Timed out.", new TimeoutException())],
        [new ServiceUnavailableApiException("Starting.")],
        [new ServiceUnavailableApiException("Full.") { ErrorCode = ApiErrorCodes.ShortLivedStoreFull }],
        [new ServiceUnavailableApiException("Starting.") { ErrorCode = ApiErrorCodes.CloudflareRangesUnavailable }],
        [new ApiException(HttpStatusCode.BadGateway, "Bad gateway.")],
        [new ApiException(HttpStatusCode.GatewayTimeout, "Gateway timeout.")],
        [new ApiException((HttpStatusCode)520, "Unknown error.")],
        [new ApiException((HttpStatusCode)524, "A timeout occurred.")],
    ];

    public static IEnumerable<object[]> OtherFailures => [
        [new OperationCanceledException()],
        [new TaskCanceledException()],
        [new IOException("The file is locked.")],
        [new WebSocketException(WebSocketError.NotAWebSocket)],
        [new InvalidOperationException()],
        [new System.Text.Json.JsonException("Invalid JSON.")],
        [new UnauthorizedApiException("Sign in again.") { ErrorCode = ApiErrorCodes.SignInRequired }],
        [new ServerErrorApiException("Unexpected.")],
        [new TooManyRequestsApiException("Wait.") { RetryAfter = TimeSpan.FromSeconds(1) }],
        [new ApiException(HttpStatusCode.BadGateway, "The app's own error.") { ErrorCode = "upstream-failed" }],
        [new ApiException((HttpStatusCode)525, "SSL handshake failed.")],
    ];

    [TestMethod]
    [DynamicData(nameof(TransientFailures))]
    public void TransientFailure_IsTransient(Exception exception)
    {
        ApiFailures.IsTransient(exception).ShouldBeTrue();
    }

    [TestMethod]
    [DynamicData(nameof(OtherFailures))]
    public void OtherFailure_IsNotTransient(Exception exception)
    {
        ApiFailures.IsTransient(exception).ShouldBeFalse();
    }

    [TestMethod]
    public async Task ServiceResponses_AreClassifiedAsThrown()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch {
            "/v1/full" => Responses.Error(HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ShortLivedStoreFull, "Try again shortly."),
            "/v1/gateway" => Responses.Content(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "text/html"),
            _ => Responses.Error(HttpStatusCode.NotFound, "Not found."),
        });

        var client = new TestApiClient(handler);

        ApiFailures.IsTransient(await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("full")))).ShouldBeTrue();
        ApiFailures.IsTransient(await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("gateway")))).ShouldBeTrue();
        ApiFailures.IsTransient(await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("missing")))).ShouldBeFalse();
    }
}
