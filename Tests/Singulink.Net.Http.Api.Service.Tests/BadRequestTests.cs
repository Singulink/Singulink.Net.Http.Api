using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Requests that minimal API endpoints cannot read are reported in the toolkit's error format with their status code: in the Production environment with a
/// general message that doesn't name the service's types, and in the Development environment with ASP.NET Core's message.
/// </summary>
[PrefixTestClass]
public sealed class BadRequestTests
{
    private const string SecretMessage = "Singulink.Contracts.Secret could not be read.";

    public sealed record Command(string Name, string CodeVerifier, int? Count = null);

    /// <summary>
    /// A handler that words bad request errors in its own way.
    /// </summary>
    public sealed class RewordingHandler(ILogger<RewordingHandler> logger, IHostEnvironment environment) : ApiExceptionHandler(logger, environment)
    {
        protected override ValueTask<ApiException?> MapAsync(HttpContext httpContext, Exception exception)
        {
            return new(exception is BadHttpRequestException ? new BadRequestApiException("Please update the app.") : null);
        }
    }

    /// <summary>
    /// A handler with the default behavior that records the exceptions it handles.
    /// </summary>
    public sealed class RecordingHandler(ILogger<RecordingHandler> logger, IHostEnvironment environment, ExceptionRecorder recorder)
        : ApiExceptionHandler(logger, environment)
    {
        protected override ValueTask<ApiException?> MapAsync(HttpContext httpContext, Exception exception)
        {
            recorder.Add(exception);
            return default;
        }
    }

    public sealed class ExceptionRecorder
    {
        private readonly List<Exception> _exceptions = [];

        public void Add(Exception exception)
        {
            lock (_exceptions)
                _exceptions.Add(exception);
        }

        /// <summary>
        /// Gets the one exception recorded since the last call.
        /// </summary>
        public Exception Take()
        {
            lock (_exceptions)
            {
                var exception = _exceptions.ShouldHaveSingleItem();
                _exceptions.Clear();

                return exception;
            }
        }
    }

    private static Task<TestWebHost> StartAsync(bool development, Action<IServiceCollection>? configureServices = null)
    {
        return TestWebHost.StartAsync(
            app => {
                app.MapPost("/commands", (Command command) => command.Name);
                app.MapGet("/count", (int count) => count);
                app.MapPost("/thrown", (string message, int status) => { throw new BadHttpRequestException(message, status); });
                app.MapPost("/thrown-json", (string message, string? path, long? line, long? position) => {
                    throw new BadHttpRequestException("Failed to read Singulink.Contracts.Secret.", new JsonException(message, path, line, position));
                });
                app.MapPost("/empty-413", (HttpContext context) => { context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; });
            },
            development: development,
            configureServices: services => {
                services.ConfigureHttpJsonOptions(o => {
                    o.SerializerOptions.RespectNullableAnnotations = true;
                    o.SerializerOptions.RespectRequiredConstructorParameters = true;
                });

                (configureServices ?? (s => s.AddSingleton<ExceptionRecorder>().AddApiExceptionHandler<RecordingHandler>()))(services);
            });
    }

    private static Task<HttpResponseMessage> PostJsonAsync(TestWebHost host, string json, string mediaType = "application/json")
    {
        return host.Client.PostAsync("/commands", new StringContent(json, Encoding.UTF8, mediaType));
    }

    private static Task<HttpResponseMessage> ThrowAsync(TestWebHost host, string message, int status, HttpContent? content = null)
    {
        return host.Client.PostAsync($"/thrown?message={Uri.EscapeDataString(message)}&status={status}", content);
    }

    /// <summary>
    /// Checks that a response is an error in the toolkit's format with the specified status code, whose message names none of the service's types outside
    /// the Development environment, and returns the message.
    /// </summary>
    private static async Task<string> ShouldBeErrorAsync(HttpResponseMessage response, HttpStatusCode statusCode, bool development = false)
    {
        response.StatusCode.ShouldBe(statusCode);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/plain");

        string message = await response.Content.ReadAsStringAsync();
        message.ShouldNotBeNullOrWhiteSpace();

        if (!development)
        {
            // The contract type is BadRequestTests.Command in the Singulink.Net.Http.Api.Service namespace, and Count is a nullable Int32.
            message.ShouldNotContain("Command");
            message.ShouldNotContain("Singulink");
            message.ShouldNotContain("System.");
            message.ShouldNotContain("Int32");
            message.ShouldNotContain("int ");
        }

        return message;
    }

    /// <summary>
    /// Checks that a response reports a request that ASP.NET Core could not read: in the Production environment with the specified message, and in the
    /// Development environment with the message of the exception that ASP.NET Core threw, followed by that of its JSON exception.
    /// </summary>
    private static async Task ShouldReportAsync(
        TestWebHost host, bool development, HttpResponseMessage response, HttpStatusCode statusCode, string productionMessage)
    {
        string message = await ShouldBeErrorAsync(response, statusCode, development);

        if (!development)
        {
            message.ShouldBe(productionMessage);
            return;
        }

        var exception = host.App.Services.GetRequiredService<ExceptionRecorder>().Take().ShouldBeAssignableTo<BadHttpRequestException>().ShouldNotBeNull();
        message.ShouldBe(exception.InnerException is JsonException json ? $"{exception.Message} {json.Message}" : exception.Message);
        message.ShouldNotBe(productionMessage);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValidBody_IsAccepted(bool development)
    {
        await using var host = await StartAsync(development);

        var response = await PostJsonAsync(host, """{"name":"n","codeVerifier":"v"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("n");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MalformedJson_IsReportedWithWhereItIs(bool development)
    {
        await using var host = await StartAsync(development);

        await ShouldReportAsync(
            host, development, await PostJsonAsync(host, """{"name":}"""), HttpStatusCode.BadRequest,
            "The request is not valid. Path: $.name | LineNumber: 0 | BytePositionInLine: 8.");

        await ShouldReportAsync(
            host, development, await PostJsonAsync(host, """{"name":"n","""), HttpStatusCode.BadRequest,
            "The request is not valid. Path: $ | LineNumber: 0 | BytePositionInLine: 11.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingRequiredProperty_IsReported(bool development)
    {
        await using var host = await StartAsync(development);

        await ShouldReportAsync(
            host, development, await PostJsonAsync(host, """{"name":"n"}"""), HttpStatusCode.BadRequest,
            "The request is not valid. Path: $ | LineNumber: 0 | BytePositionInLine: 12.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NullForNonNullableProperty_IsReportedWithItsPath(bool development)
    {
        await using var host = await StartAsync(development);

        await ShouldReportAsync(
            host, development, await PostJsonAsync(host, """{"name":null,"codeVerifier":"v"}"""), HttpStatusCode.BadRequest,
            "The request is not valid. Path: $.name | LineNumber: 0 | BytePositionInLine: 12.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValueOfTheWrongKind_IsReportedWithItsPath(bool development)
    {
        await using var host = await StartAsync(development);

        await ShouldReportAsync(
            host, development, await PostJsonAsync(host, """{"name":"n","codeVerifier":"v","count":"x"}"""), HttpStatusCode.BadRequest,
            "The request is not valid. Path: $.count | LineNumber: 0 | BytePositionInLine: 42.");

        await ShouldReportAsync(
            host, development, await PostJsonAsync(host, "[1]"), HttpStatusCode.BadRequest,
            "The request is not valid. Path: $ | LineNumber: 0 | BytePositionInLine: 1.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WrongContentType_IsReportedAsUnsupportedMediaType(bool development)
    {
        await using var host = await StartAsync(development);

        // Routing answers with an empty response, which has no message of ASP.NET Core's.
        (await ShouldBeErrorAsync(await PostJsonAsync(host, "name", "text/plain"), HttpStatusCode.UnsupportedMediaType))
            .ShouldBe("""The request content type "text/plain; charset=utf-8" is not supported.""");

        // Routing lets a body without a content type through, and the endpoint then throws.
        var withoutContentType = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"));

        await ShouldReportAsync(
            host, development, await host.Client.PostAsync("/commands", withoutContentType), HttpStatusCode.UnsupportedMediaType,
            "The request has no content type.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingBody_IsReported(bool development)
    {
        await using var host = await StartAsync(development);

        await ShouldReportAsync(
            host, development, await host.Client.PostAsync("/commands", new ByteArrayContent([])), HttpStatusCode.BadRequest,
            "The request is not valid.");
    }

    [TestMethod]
    public async Task EmptyErrorResponsesOfOtherStatuses_AreLeftAsTheyAre()
    {
        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/empty-400", (HttpContext context) => {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }));

        var response = await host.Client.GetAsync("/empty-400");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingAndUnreadableQueryValues_AreReported(bool development)
    {
        await using var host = await StartAsync(development);

        await ShouldReportAsync(host, development, await host.Client.GetAsync("/count"), HttpStatusCode.BadRequest, "The request is not valid.");
        await ShouldReportAsync(host, development, await host.Client.GetAsync("/count?count=many"), HttpStatusCode.BadRequest, "The request is not valid.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TooLarge_IsReportedWithItsStatusCode(bool development)
    {
        await using var host = await StartAsync(development);

        const string KestrelMessage = "Request body too large. The max request body size is 100 bytes.";
        (await ShouldBeErrorAsync(await ThrowAsync(host, KestrelMessage, 413), HttpStatusCode.RequestEntityTooLarge))
            .ShouldBe(development ? KestrelMessage : "The request is too large.");

        (await ShouldBeErrorAsync(await host.Client.PostAsync("/empty-413", null), HttpStatusCode.RequestEntityTooLarge))
            .ShouldBe("The request is too large.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ThrownExceptions_HaveGeneralMessagesOutsideDevelopment(bool development)
    {
        await using var host = await StartAsync(development);

        async Task ShouldBeAsync(HttpResponseMessage response, HttpStatusCode statusCode, string productionMessage, string developmentMessage)
        {
            (await ShouldBeErrorAsync(response, statusCode, development)).ShouldBe(development ? developmentMessage : productionMessage);
        }

        await ShouldBeAsync(await ThrowAsync(host, SecretMessage, 400), HttpStatusCode.BadRequest, "The request is not valid.", SecretMessage);
        await ShouldBeAsync(await ThrowAsync(host, SecretMessage, 408), HttpStatusCode.RequestTimeout, "The request was not received in time.", SecretMessage);
        await ShouldBeAsync(await ThrowAsync(host, SecretMessage, 413), HttpStatusCode.RequestEntityTooLarge, "The request is too large.", SecretMessage);
        await ShouldBeAsync(await ThrowAsync(host, SecretMessage, 415), HttpStatusCode.UnsupportedMediaType, "The request has no content type.", SecretMessage);

        await ShouldBeAsync(
            await ThrowAsync(host, SecretMessage, 415, new StringContent("x", Encoding.UTF8, "text/x-secret")),
            HttpStatusCode.UnsupportedMediaType,
            """The request content type "text/x-secret; charset=utf-8" is not supported.""",
            SecretMessage);

        await ShouldBeAsync(
            await host.Client.PostAsync($"/thrown-json?message={Uri.EscapeDataString(SecretMessage)}&path=%24.items%5B0%5D&line=2&position=5", null),
            HttpStatusCode.BadRequest,
            "The request is not valid. Path: $.items[0] | LineNumber: 2 | BytePositionInLine: 5.",
            $"Failed to read Singulink.Contracts.Secret. {SecretMessage}");

        await ShouldBeAsync(
            await host.Client.PostAsync($"/thrown-json?message={Uri.EscapeDataString(SecretMessage)}", null),
            HttpStatusCode.BadRequest,
            "The request is not valid.",
            $"Failed to read Singulink.Contracts.Secret. {SecretMessage}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BadRequest_KeepsTheOriginalExceptionAsInnerException(bool development)
    {
        var toApiException = typeof(ApiExceptionHandler).Assembly
            .GetType("Singulink.Net.Http.Api.Service.BadRequestMapping").ShouldNotBeNull()
            .GetMethod("ToApiException", BindingFlags.Public | BindingFlags.Static).ShouldNotBeNull();

        ApiException Map(BadHttpRequestException exception) =>
            (ApiException)toApiException.Invoke(null, [new DefaultHttpContext(), exception, development]).ShouldNotBeNull();

        var badRequest = new BadHttpRequestException(SecretMessage);
        var badRequestApiException = Map(badRequest).ShouldBeOfType<BadRequestApiException>();

        badRequestApiException.InnerException.ShouldBeSameAs(badRequest);
        badRequestApiException.Message.ShouldBe(development ? SecretMessage : "The request is not valid.");

        var unsupported = new BadHttpRequestException(SecretMessage, StatusCodes.Status415UnsupportedMediaType);
        var unsupportedApiException = Map(unsupported).ShouldBeOfType<UnsupportedMediaTypeApiException>();

        unsupportedApiException.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        unsupportedApiException.InnerException.ShouldBeSameAs(unsupported);
    }

    [TestMethod]
    public void BadRequest_StatusSelectsTheExceptionType()
    {
        var mapping = typeof(ApiExceptionHandler).Assembly.GetType("Singulink.Net.Http.Api.Service.BadRequestMapping").ShouldNotBeNull();
        var toApiException = mapping.GetMethod("ToApiException", BindingFlags.Public | BindingFlags.Static).ShouldNotBeNull();
        var fromEmptyResponse = mapping.GetMethod("FromEmptyResponse", BindingFlags.Public | BindingFlags.Static).ShouldNotBeNull();

        ApiException Map(int status) =>
            (ApiException)toApiException.Invoke(null, [new DefaultHttpContext(), new BadHttpRequestException("Message.", status), false]).ShouldNotBeNull();

        Map(400).ShouldBeOfType<BadRequestApiException>();
        Map(413).ShouldBeOfType<ContentTooLargeApiException>();
        Map(415).ShouldBeOfType<UnsupportedMediaTypeApiException>();

        // Statuses that only ASP.NET Core chooses are passed through as they are.
        var timeout = Map(408).ShouldBeOfType<ApiException>();
        timeout.StatusCode.ShouldBe(HttpStatusCode.RequestTimeout);

        ApiException? MapEmpty(int status) => (ApiException?)fromEmptyResponse.Invoke(null, [new DefaultHttpContext { Response = { StatusCode = status } }]);

        MapEmpty(413).ShouldBeOfType<ContentTooLargeApiException>().Message.ShouldBe("The request is too large.");
        MapEmpty(415).ShouldBeOfType<UnsupportedMediaTypeApiException>().Message.ShouldBe("The request has no content type.");
        MapEmpty(404).ShouldBeNull();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BadRequests_AreNotLoggedAsErrors(bool development)
    {
        await using var host = await StartAsync(development);

        await PostJsonAsync(host, """{"name":}""");
        await host.Client.GetAsync("/count");

        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandlerMapping_TakesPrecedence(bool development)
    {
        await using var host = await StartAsync(development, s => s.AddApiExceptionHandler<RewordingHandler>());

        (await ShouldBeErrorAsync(await PostJsonAsync(host, """{"name":}"""), HttpStatusCode.BadRequest)).ShouldBe("Please update the app.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WithoutHandler_BadRequestsAreStillReported(bool development)
    {
        await using var host = await StartAsync(development, _ => { });

        (await ShouldBeErrorAsync(await ThrowAsync(host, SecretMessage, 400), HttpStatusCode.BadRequest, development))
            .ShouldBe(development ? SecretMessage : "The request is not valid.");

        // Development makes minimal APIs throw on bad requests by default.
        if (development)
        {
            (await ShouldBeErrorAsync(await PostJsonAsync(host, """{"name":"n"}"""), HttpStatusCode.BadRequest, development))
                .ShouldContain("codeVerifier");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddApiExceptionHandler_SetsThrowOnBadRequest(bool development)
    {
        var builder = CreateBuilder(development);
        builder.Services.AddApiExceptionHandler();
        builder.Services.AddApiExceptionHandler();

        using var app = builder.Build();
        app.Services.GetRequiredService<IOptions<RouteHandlerOptions>>().Value.ThrowOnBadRequest.ShouldBeTrue();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ThrowOnBadRequest_LastConfigurationWins(bool development, bool configuredFirst)
    {
        var builder = CreateBuilder(development);

        if (configuredFirst)
            builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);

        builder.Services.AddApiExceptionHandler();

        if (!configuredFirst)
        {
            builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
            builder.Services.AddApiExceptionHandler();
        }

        using var app = builder.Build();
        app.Services.GetRequiredService<IOptions<RouteHandlerOptions>>().Value.ThrowOnBadRequest.ShouldBe(configuredFirst);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BodyOverKestrelLimit_IsReportedAsTooLarge(bool development)
    {
        var builder = CreateBuilder(development);

        builder.WebHost.UseKestrel(o => o.Limits.MaxRequestBodySize = 64).UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddApiExceptionHandler();
        builder.Services.AddApiResponseHandling();

        await using var app = builder.Build();
        app.MapPost("/commands", (Command command) => command.Name);
        app.UseApiResponseHandling();
        await app.StartAsync();

        string address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        string json = $$"""{"name":"{{new string('n', 200)}}","codeVerifier":"v"}""";
        var response = await client.PostAsync("/commands", new StringContent(json, Encoding.UTF8, "application/json"));

        (await ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge)).ShouldBe("The request is too large.");
    }

    private static WebApplicationBuilder CreateBuilder(bool development) => WebApplication.CreateSlimBuilder(new WebApplicationOptions {
        EnvironmentName = development ? Environments.Development : Environments.Production,
    });
}
