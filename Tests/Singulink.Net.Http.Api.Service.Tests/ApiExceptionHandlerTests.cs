using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable RCS1194 // Implement exception constructors

[PrefixTestClass]
public sealed class ApiExceptionHandlerTests
{
    public sealed record Item(int Id);

    public sealed class AppValidationException(string message) : Exception(message);

    /// <summary>
    /// Handler with an application-specific mapping and a custom server error message.
    /// </summary>
    public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger, IHostEnvironment environment) : ApiExceptionHandler(logger, environment)
    {
        protected override ValueTask<ApiException?> MapAsync(HttpContext httpContext, Exception exception)
        {
            return new(exception is AppValidationException ? new ValidationApiException(exception.Message) : null);
        }

        protected override string CreateServerErrorMessage(Guid referenceId) => $"Custom message {referenceId}";
    }

    private static async IAsyncEnumerable<Item> FailAfterFirstAsync(Exception exception, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new Item(1);
        await Task.Yield();
        throw exception;
    }

    [TestMethod]
    public async Task Default_UnexpectedException_LogsWithReferenceIdAndReportsServerError()
    {
        await using var host = await TestWebHost.StartAsync(
            app => app.MapGet("/boom", Item () => throw new InvalidOperationException("secret details")),
            configureServices: s => s.AddApiExceptionHandler());

        var response = await host.Client.GetAsync("/boom");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");

        string body = await response.Content.ReadAsStringAsync();
        body.ShouldStartWith("An unexpected error has occurred and has been logged. Reference ID: ");
        body.ShouldEndWith(".");
        body.ShouldNotContain("secret details");

        var referenceId = Guid.Parse(body[(body.LastIndexOf(' ') + 1)..^1]);
        referenceId.Version.ShouldBe(7);
        response.Headers.GetValues("Reference-Id").ShouldBe([referenceId.ToString("D")]);

        var log = host.WarningLogs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Error);
        log.Category.ShouldContain("DefaultApiExceptionHandler");
        log.Exception.ShouldBeOfType<InvalidOperationException>();
        log.Message.ShouldContain("GET /boom");
        log.Message.ShouldContain(referenceId.ToString());
    }

    [TestMethod]
    public async Task Default_UnexpectedExceptionInStream_ReportsServerErrorRecordWithReferenceId()
    {
        await using var host = await TestWebHost.StartAsync(
            app => app.MapGet("/stream", () => FailAfterFirstAsync(new InvalidOperationException("secret details"))),
            configureServices: s => s.AddApiExceptionHandler());

        var response = await host.Client.GetAsync("/stream");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);

        var error = JsonDocument.Parse(lines[1]).RootElement.GetProperty("error");
        error.GetProperty("status").GetInt32().ShouldBe(500);
        error.GetProperty("message").GetString()!.ShouldStartWith("An unexpected error has occurred and has been logged. Reference ID: ");
        error.TryGetProperty("code", out _).ShouldBeFalse();

        string referenceId = error.GetProperty("referenceId").GetString()!;
        Guid.Parse(referenceId).Version.ShouldBe(7);
        error.GetProperty("message").GetString()!.ShouldEndWith($"{referenceId}.");

        var log = host.WarningLogs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Error);
        log.Message.ShouldContain("GET /stream");
    }

    [TestMethod]
    public async Task Default_ApiException_IsReportedAsIsWithoutLogging()
    {
        await using var host = await TestWebHost.StartAsync(
            app => app.MapGet("/nope", Item () => throw new ForbiddenApiException("no access") { ErrorCode = "denied" }),
            configureServices: s => s.AddApiExceptionHandler());

        var response = await host.Client.GetAsync("/nope");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).ShouldBe("[denied] no access");
        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Default_InDevelopment_PropagatesUnexpectedExceptions()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.MapGet("/boom", Item () => throw new InvalidOperationException("secret details"));
                app.MapGet("/stream", () => FailAfterFirstAsync(new InvalidOperationException("secret details")));
            },
            development: true,
            configureServices: s => s.AddApiExceptionHandler());

        // Regular responses propagate to the developer exception page (added automatically by WebApplication in Development), which shows the details.
        var response = await host.Client.GetAsync("/boom");
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).ShouldContain("secret details");

        // Streams that have already started report the full details.
        string body = await host.Client.GetStringAsync("/stream");
        body.ShouldContain("secret details");
        body.ShouldContain(nameof(InvalidOperationException));

        host.WarningLogs.Where(l => l.Category.Contains("DefaultApiExceptionHandler")).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Derived_MapsApplicationExceptions_AndCustomizesServerErrorMessage()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.MapGet("/invalid", Item () => throw new AppValidationException("bad input"));
                app.MapGet("/invalid-stream", () => FailAfterFirstAsync(new AppValidationException("bad input")));
                app.MapGet("/boom", Item () => throw new InvalidOperationException("secret details"));
            },
            configureServices: s => s.AddApiExceptionHandler<AppExceptionHandler>());

        var invalid = await host.Client.GetAsync("/invalid");
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await invalid.Content.ReadAsStringAsync()).ShouldBe("bad input");

        string stream = await host.Client.GetStringAsync("/invalid-stream");
        stream.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].ShouldBe("""{"error":{"status":422,"message":"bad input"}}""");

        var boom = await host.Client.GetAsync("/boom");
        boom.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await boom.Content.ReadAsStringAsync()).ShouldStartWith("Custom message ");

        // Only the unexpected exception is logged.
        host.WarningLogs.ShouldHaveSingleItem().Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [TestMethod]
    public async Task Derived_WordsApiExceptionsByTheirErrorCodes()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.MapGet("/locked", Item () => throw new ConflictApiException("Locked.") { ErrorCode = "document-locked" });
                app.MapGet("/locked-stream", () => FailAfterFirstAsync(new ConflictApiException("Locked.") { ErrorCode = "document-locked" }));
                app.MapGet("/nope", Item () => throw new ForbiddenApiException("no access") { ErrorCode = "denied" });
            },
            configureServices: s => s.AddApiExceptionHandler<WordingExceptionHandler>());

        var locked = await host.Client.GetAsync("/locked");
        locked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await locked.Content.ReadAsStringAsync()).ShouldBe("[document-locked] Someone else is editing this document.");

        string stream = await host.Client.GetStringAsync("/locked-stream");
        stream.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].ShouldBe(
            """{"error":{"status":409,"code":"document-locked","message":"Someone else is editing this document."}}""");

        // Other API exceptions are reported as they are, and MapAsync isn't called for them.
        var nope = await host.Client.GetAsync("/nope");
        nope.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await nope.Content.ReadAsStringAsync()).ShouldBe("[denied] no access");
    }

    [TestMethod]
    public async Task ServerErrorException_IncludesOriginalAsInnerException()
    {
        Exception? handled = null;

        await using var host = await TestWebHost.StartAsync(
            app => app.MapGet("/boom", Item () => throw new InvalidOperationException("secret details")),
            configureServices: s => {
                s.AddSingleton<IApiExceptionHandler>(sp => new InspectingHandler(
                    sp.GetRequiredService<ILogger<InspectingHandler>>(), sp.GetRequiredService<IHostEnvironment>(), ex => handled = ex));
            });

        await host.Client.GetAsync("/boom");

        var serverError = handled.ShouldBeOfType<ServerErrorApiException>();
        serverError.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    /// <summary>
    /// Handler that words the API exceptions with one error code in its own way, and fails the test if <see cref="ApiExceptionHandler.MapAsync"/> is called
    /// for an API exception.
    /// </summary>
    private sealed class WordingExceptionHandler(ILogger<WordingExceptionHandler> logger, IHostEnvironment environment)
        : ApiExceptionHandler(logger, environment)
    {
        protected override ValueTask<ApiException> MapApiExceptionAsync(HttpContext httpContext, ApiException exception) => new(exception.ErrorCode switch {
            "document-locked" => exception.WithMessage("Someone else is editing this document."),
            _ => exception,
        });

        protected override ValueTask<ApiException?> MapAsync(HttpContext httpContext, Exception exception)
        {
            exception.ShouldNotBeAssignableTo<ApiException>();
            return default;
        }
    }

    private sealed class InspectingHandler(ILogger logger, IHostEnvironment environment, Action<Exception?> inspect) : ApiExceptionHandler(logger, environment)
    {
        protected override async ValueTask<ApiException?> HandleUnexpectedAsync(HttpContext httpContext, Exception exception)
        {
            var result = await base.HandleUnexpectedAsync(httpContext, exception);
            inspect(result);
            return result;
        }
    }
}
