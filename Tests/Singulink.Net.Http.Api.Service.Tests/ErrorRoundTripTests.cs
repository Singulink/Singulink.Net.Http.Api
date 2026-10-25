using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.Client;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Errors that a service writes, read by the toolkit's client over HTTP: each arrives as the exception type of its status, with its message, error code,
/// retry time and reference ID.
/// </summary>
[PrefixTestClass]
public sealed class ErrorRoundTripTests
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(90);

    [TestMethod]
    [DataRow(400, typeof(BadRequestApiException))]
    [DataRow(409, typeof(ConflictApiException))]
    [DataRow(413, typeof(ContentTooLargeApiException))]
    [DataRow(415, typeof(UnsupportedMediaTypeApiException))]
    [DataRow(429, typeof(TooManyRequestsApiException))]
    [DataRow(500, typeof(ServerErrorApiException))]
    [DataRow(503, typeof(ServiceUnavailableApiException))]
    public async Task ThrownException_ArrivesAsTheSameTypeWithRetryAfter(int status, Type exceptionType)
    {
        await using var app = await StartAsync(app => app.MapGet("/fail", (int status) => { throw Create(status); }));
        var client = new LoopbackApiClient(app);

        var ex = await Should.ThrowAsync<ApiException>(() => client.GetAsync($"fail?status={status}"));

        ex.ShouldBeOfType(exceptionType);
        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        ex.ErrorCode.ShouldBe("the-code");
        ex.Message.ShouldBe("The message.");
        ex.RetryAfter.ShouldBe(RetryAfter);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("the-code")]
    public async Task MessageThatIsNotAscii_IsSentInUtf8WithItsCharset_AndArrivesIntact(string? errorCode)
    {
        const string Message = "Café — ✓";
        await using var app = await StartAsync(app => app.MapGet("/fail", () => { throw new ConflictApiException(Message) { ErrorCode = errorCode }; }));
        var client = new LoopbackApiClient(app);

        // As a browser or another HTTP client receives it.
        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(new Uri(client.BaseAddress, "fail"));

        var contentType = response.Content.Headers.ContentType.ShouldNotBeNull();
        contentType.MediaType.ShouldBe("text/plain");
        contentType.CharSet.ShouldBe("utf-8");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Encoding.UTF8.GetBytes(errorCode is null ? Message : $"[{errorCode}] {Message}"));

        var ex = await Should.ThrowAsync<ConflictApiException>(() => client.GetAsync("fail"));

        ex.Message.ShouldBe(Message);
        ex.ErrorCode.ShouldBe(errorCode);
    }

    [TestMethod]
    public async Task ExceptionWithoutRetryAfter_ArrivesWithoutRetryAfter()
    {
        await using var app = await StartAsync(app => app.MapGet("/fail", () => { throw new ConflictApiException("Already exists."); }));
        var client = new LoopbackApiClient(app);

        var ex = await Should.ThrowAsync<ConflictApiException>(() => client.GetAsync("fail"));

        ex.RetryAfter.ShouldBeNull();
    }

    [TestMethod]
    public async Task UserChanged_ArrivesWithTheUserIdPreconditionOfTheRequest_AndNotWithTheServicesExpectedUserId()
    {
        await using var app = await StartAsync(app => app.MapGet("/fail", () => {
            throw new UserChangedApiException("The user changed.") { ExpectedUserId = "9" };
        }));

        var client = new LoopbackApiClient(app);
        var userClient = new LoopbackUserApiClient(client, 5);

        // As a browser or another HTTP client receives it.
        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(new Uri(client.BaseAddress, "fail?if-userId=5"));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await response.Content.ReadAsStringAsync()).ShouldBe("The user changed.");
        response.Headers.Select(h => h.Key).ShouldNotContain(k => k.Contains("user", StringComparison.OrdinalIgnoreCase));

        var ofTheUser = await Should.ThrowAsync<UserChangedApiException>(() => userClient.GetAsync("fail"));
        var withoutPrecondition = await Should.ThrowAsync<UserChangedApiException>(() => client.GetAsync("fail"));

        ofTheUser.ExpectedUserId.ShouldBe("5");
        ofTheUser.Message.ShouldBe("The user changed.");
        withoutPrecondition.ExpectedUserId.ShouldBeNull();
    }

    [TestMethod]
    public async Task ReferenceId_Arrives()
    {
        var referenceId = Guid.NewGuid();
        await using var app = await StartAsync(app => app.MapGet("/fail", () => {
            throw new ConflictApiException("Conflict.") { ReferenceId = referenceId };
        }));
        var client = new LoopbackApiClient(app);

        var ex = await Should.ThrowAsync<ConflictApiException>(() => client.GetAsync("fail"));

        ex.ReferenceId.ShouldBe(referenceId);
    }

    [TestMethod]
    public async Task UnexpectedException_ArrivesWithTheReferenceIdOfItsMessage()
    {
        await using var app = await StartAsync(app => app.MapGet("/fail", () => { throw new InvalidOperationException("Secret details."); }));
        var client = new LoopbackApiClient(app);

        var ex = await Should.ThrowAsync<ServerErrorApiException>(() => client.GetAsync("fail"));

        var referenceId = ex.ReferenceId.ShouldNotBeNull();
        ex.Message.ShouldContain(referenceId.ToString());
        ex.Message.ShouldNotContain("Secret details.");
    }

    [TestMethod]
    public async Task RetryAfter_ArrivesInWholeSecondsRoundedUp()
    {
        await using var app = await StartAsync(app => app.MapGet("/fail", () => {
            throw new ServiceUnavailableApiException("Starting.") { RetryAfter = TimeSpan.FromMilliseconds(1500) };
        }));

        var client = new LoopbackApiClient(app);

        var ex = await Should.ThrowAsync<ServiceUnavailableApiException>(() => client.GetAsync("fail"));
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task RateLimitRejection_ArrivesAsTooManyRequestsWithRetryAfter()
    {
        await using var app = await StartAsync(
            app => {
                app.UseRateLimiter();
                app.MapGet("/limited", () => "ok").RequireRateLimiting("limit");
            },
            services => {
                // A clock that stands still, so that the window cannot end between the two requests.
                services.AddSingleton<TimeProvider>(new TestClock());

                services.AddRateLimiter(o => o
                    .AddClientFixedWindowLimiter("limit", c => {
                        c.PermitLimit = 1;
                        c.ExemptPrivateNetworks = false;
                    })
                    .SetApiRejectionResponse());
            });

        var client = new LoopbackApiClient(app);
        await client.GetAsync("limited");

        var ex = await Should.ThrowAsync<TooManyRequestsApiException>(() => client.GetAsync("limited"));

        ex.Message.ShouldBe("Too many requests. Please wait a moment and try again.");
        ex.ErrorCode.ShouldBe(ApiErrorCodes.RateLimited);
        ex.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
        ex.RetryAfter.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
    }

    private static ApiException Create(int status) => status switch {
        400 => new BadRequestApiException("The message.") { ErrorCode = "the-code", RetryAfter = RetryAfter },
        409 => new ConflictApiException("The message.") { ErrorCode = "the-code", RetryAfter = RetryAfter },
        413 => new ContentTooLargeApiException("The message.") { ErrorCode = "the-code", RetryAfter = RetryAfter },
        415 => new UnsupportedMediaTypeApiException("The message.") { ErrorCode = "the-code", RetryAfter = RetryAfter },
        429 => new TooManyRequestsApiException("The message.") { ErrorCode = "the-code", RetryAfter = RetryAfter },
        500 => new ServerErrorApiException("The message.") { ErrorCode = "the-code", RetryAfter = RetryAfter },
        503 => new ServiceUnavailableApiException("The message.") { ErrorCode = "the-code", RetryAfter = RetryAfter },
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>
    /// Starts the app on a free loopback port, so that the toolkit's client reaches it over HTTP as it would reach a real service.
    /// </summary>
    private static async Task<WebApplication> StartAsync(Action<WebApplication> map, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });

        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddApiExceptionHandler();
        builder.Services.AddApiResponseHandling();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        map(app);
        app.UseApiResponseHandling();

        await app.StartAsync();
        return app;
    }

    private sealed class LoopbackApiClient(WebApplication app) : ApiClientBase(TestSessionTokenJsonContext.Default)
    {
        private readonly Uri _baseAddress = new(app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>()
            .Addresses.First() + "/");

        public Uri BaseAddress => _baseAddress;

        public Task GetAsync(string path) => SendAsync(CreateRequest(HttpMethod.Get, path));

        protected override Uri GetBaseAddress() => _baseAddress;
    }

    private sealed class LoopbackUserApiClient(LoopbackApiClient parent, int userId) : ApiClientBase(parent, userIdPrecondition: userId)
    {
        public Task GetAsync(string path) => SendAsync(CreateRequest(HttpMethod.Get, path));
    }
}
