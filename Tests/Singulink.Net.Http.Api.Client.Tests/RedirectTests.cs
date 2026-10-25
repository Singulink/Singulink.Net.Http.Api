namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Runs requests through the client's shared HTTP handler against a server on a loopback address, to check how redirects are handled.
/// </summary>
[PrefixTestClass]
public sealed class RedirectTests
{
    private static readonly IPAddress OtherHostAddress = IPAddress.Parse("127.0.0.2");

    private static TestApiClient CreateClient(Uri baseAddress, string? sessionToken) => new(handler: null, sessionToken) {
        BaseAddress = baseAddress,
    };

    [TestMethod]
    public async Task CrossHostRedirect_IsNotFollowed()
    {
        using var otherHost = new RawHttpServer(OtherHostAddress, _ =>
            "HTTP/1.1 401 Unauthorized\r\nContent-Type: text/plain; format=error-code\r\nSet-Cookie: __Host-session-token=planted; path=/\r\n" +
            "Connection: close\r\nContent-Length: 27\r\n\r\n[sign-in-required] Sign in.");

        var location = new Uri(otherHost.BaseAddress, "landing");

        using var host = new RawHttpServer(IPAddress.Loopback, _ =>
            $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");

        var client = CreateClient(host.BaseAddress, "secret-token");

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.ShouldBeOfType<ApiException>();
        ex.StatusCode.ShouldBe(HttpStatusCode.Found);
        ex.Message.ShouldBe($"The service redirected the request to {location} (302 Found), and redirects are not followed.");

        host.Requests.ShouldHaveSingleItem().ShouldContain("Cookie: __Host-session-token=secret-token");
        otherHost.Requests.ShouldBeEmpty();
        client.SessionToken.ShouldBe("secret-token");
    }

    [TestMethod]
    public async Task SetCookieOnRedirect_IsPickedUp()
    {
        using var host = new RawHttpServer(IPAddress.Loopback, head => head.StartsWith("GET /landing", StringComparison.Ordinal) ?
            "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n" :
            "HTTP/1.1 307 Temporary Redirect\r\nLocation: /landing\r\nSet-Cookie: __Host-session-token=refreshed; path=/\r\n" +
            "Connection: close\r\nContent-Length: 0\r\n\r\n");

        var client = CreateClient(host.BaseAddress, "saved-token");

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        ex.Message.ShouldBe($"The service redirected the request to {host.BaseAddress}landing (307 Temporary Redirect), and redirects are not followed.");
        client.SessionToken.ShouldBe("refreshed");
        host.Requests.ShouldHaveSingleItem();
    }
}
