using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// An exception thrown after the client aborted the request that resolves to an API error, as is or through the exception handler, ends the request without
/// a response or a log entry, a cancellation or I/O exception propagates without the handler and isn't logged, and other exceptions thrown then still
/// propagate to the server, which logs them.
/// </summary>
[PrefixTestClass]
public sealed class AbortedRequestTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ApiExceptionAfterTheClientAborted_IsNotLogged_WhileOtherExceptionsStillPropagate()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.MapGet("/not-found", (HttpContext context) => ThrowAfterAbortAsync(context, new NotFoundApiException("The upload is gone.")));
                app.MapGet("/boom", (HttpContext context) => ThrowAfterAbortAsync(context, new InvalidOperationException("Something broke.")));
            },
            kestrel: true);

        await Should.ThrowAsync<HttpRequestException>(host.Client.GetAsync("/not-found"));
        await Should.ThrowAsync<HttpRequestException>(host.Client.GetAsync("/boom"));

        // Stopping waits for the requests to end, and the server logs an exception that propagated before a request ends.
        await host.App.StopAsync();

        var log = host.Logs.Where(l => l.Level >= LogLevel.Error).ShouldHaveSingleItem();
        log.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [TestMethod]
    public async Task ExceptionThatTheHandlerMapsAfterTheClientAborted_IsNotLogged_AndTheHandlerIsCalled()
    {
        var handler = new MappingHandler();

        await using var host = await TestWebHost.StartAsync(
            app => app.MapGet("/gone", (HttpContext context) => ThrowAfterAbortAsync(context, new KeyNotFoundException("The upload is gone."))),
            handler,
            kestrel: true);

        await Should.ThrowAsync<HttpRequestException>(host.Client.GetAsync("/gone"));
        await host.App.StopAsync();

        host.Logs.Where(l => l.Level >= LogLevel.Error).ShouldBeEmpty();
        handler.Handled.ShouldHaveSingleItem().ShouldBeOfType<KeyNotFoundException>();
    }

    [TestMethod]
    public async Task CancellationAndIOExceptionsAfterTheClientAborted_WithTheDefaultHandler_AreNotLogged()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.MapGet("/cancelled", (HttpContext context) => ThrowAfterAbortAsync(context, new OperationCanceledException(context.RequestAborted)));
                app.MapGet("/io", (HttpContext context) => ThrowAfterAbortAsync(context, new IOException("The client is gone.")));
            },
            configureServices: services => services.AddApiExceptionHandler(),
            kestrel: true);

        await Should.ThrowAsync<HttpRequestException>(host.Client.GetAsync("/cancelled"));
        await Should.ThrowAsync<HttpRequestException>(host.Client.GetAsync("/io"));
        await host.App.StopAsync();

        host.Logs.Where(l => l.Level >= LogLevel.Warning).ShouldBeEmpty();
    }

    private static async Task ThrowAfterAbortAsync(HttpContext context, Exception exception)
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (context.RequestAborted.Register(aborted.SetResult))
        {
            context.Abort();
            await aborted.Task.WaitAsync(Timeout);
        }

        throw exception;
    }

    private sealed class MappingHandler : IApiExceptionHandler
    {
        public ConcurrentQueue<Exception> Handled { get; } = new();

        public ValueTask<ApiException?> HandleAsync(HttpContext httpContext, Exception exception)
        {
            Handled.Enqueue(exception);
            return ValueTask.FromResult<ApiException?>(exception is KeyNotFoundException ? new NotFoundApiException(exception.Message) : null);
        }
    }
}
