using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The CORS policy of <see cref="WebApplicationExtensions.UseAllowedOrigins"/>.
/// </summary>
[PrefixTestClass]
public sealed class AllowedOriginsTests
{
    [TestMethod]
    public async Task AllowedOrigin_CanReadRetryAfter()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.UseAllowedOrigins();
                app.MapGet("/busy", () => { throw new ServiceUnavailableApiException("Busy.") { RetryAfter = TimeSpan.FromSeconds(5) }; });
            },
            configureServices: services => services.AddAllowedOrigins("app.example.com"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/busy");
        request.Headers.Add("Origin", "https://app.example.com");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["https://app.example.com"]);
        response.Headers.GetValues("Access-Control-Expose-Headers").ShouldBe(["Retry-After,Reference-Id,Location"]);
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow("localhost")]
    [DataRow("http://localhost:5173")]
    [DataRow("127.0.0.1")]
    [DataRow("[::1]")]
    public async Task LoopbackOrigin_OutsideDevelopment_IsLoggedAsWarning(string loopbackOrigin)
    {
        await using var host = await StartAsync(development: false, "app.example.com", loopbackOrigin);

        host.WarningLogs.ShouldHaveSingleItem().Message.ShouldContain("loopback host");
    }

    [TestMethod]
    public async Task LoopbackOrigin_InDevelopment_IsNotLogged()
    {
        await using var host = await StartAsync(development: true, "app.example.com", "localhost");

        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("*.localhost")]
    [DataRow("localhost.example.com")]
    public async Task OtherOrigins_OutsideDevelopment_AreNotLogged(string origin)
    {
        await using var host = await StartAsync(development: false, "app.example.com", origin);

        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("https://app.example.com", true)]
    [DataRow("http://app.example.com", false)]
    [DataRow("https://app.example.com.evil.test", false)]
    [DataRow("http://localhost:5173", true)]
    public async Task Cors_EchoesOnlyAllowedOrigins(string origin, bool echoed)
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.UseAllowedOrigins();
                app.MapGet("/ping", () => "pong");
            },
            configureServices: services => services.AddAllowedOrigins("app.example.com", "localhost"));

        using var request = new HttpRequestMessage(HttpMethod.Options, "/ping");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await host.Client.SendAsync(request);

        if (echoed)
        {
            response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([origin]);
            response.Headers.GetValues("Access-Control-Allow-Credentials").ShouldBe(["true"]);
        }
        else
        {
            response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        }
    }

    [TestMethod]
    public async Task Preflight_IsCachedForTwoHours()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.UseAllowedOrigins();
                app.MapPut("/uploads/{id}", () => Results.NoContent());
            },
            configureServices: services => services.AddAllowedOrigins("app.example.com"));

        using var request = new HttpRequestMessage(HttpMethod.Options, "/uploads/1");
        request.Headers.Add("Origin", "https://app.example.com");
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "content-range");

        using var response = await host.Client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["https://app.example.com"]);
        response.Headers.GetValues("Access-Control-Allow-Methods").ShouldBe(["PUT"]);
        response.Headers.GetValues("Access-Control-Allow-Headers").ShouldBe(["content-range"]);
        response.Headers.GetValues("Access-Control-Max-Age").ShouldBe(["7200"]);
    }

    private static Task<TestWebHost> StartAsync(bool development, params string[] allowedOrigins)
    {
        return TestWebHost.StartAsync(
            app => {
                app.UseAllowedOrigins();
                app.MapGet("/ping", () => "pong");
            },
            development: development,
            configureServices: services => services.AddAllowedOrigins(allowedOrigins));
    }
}
