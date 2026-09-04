using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// An in-process web application (using the test server) with API response handling applied, for testing endpoints end to end.
/// </summary>
public sealed class TestWebHost : IAsyncDisposable
{
    private readonly CapturingLoggerProvider _logs;

    private TestWebHost(WebApplication app, CapturingLoggerProvider logs)
    {
        App = app;
        Client = app.GetTestClient();
        _logs = logs;
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    /// <summary>
    /// Gets the log entries written by the application so far.
    /// </summary>
    public IReadOnlyList<LogEntry> Logs => _logs.Entries;

    /// <summary>
    /// Gets the log entries of warning level or higher written by the application so far (i.e. excluding the framework's informational request logging).
    /// </summary>
    public IReadOnlyList<LogEntry> WarningLogs => _logs.Entries.Where(l => l.Level >= LogLevel.Warning).ToList();

    /// <summary>
    /// Gets the built endpoints (building them applies the endpoint conventions).
    /// </summary>
    public IReadOnlyList<Endpoint> Endpoints => ((IEndpointRouteBuilder)App).DataSources.SelectMany(s => s.Endpoints).ToList();

    /// <summary>
    /// Starts a web application with the specified endpoints mapped and <c>UseApiResponseHandling</c> applied.
    /// </summary>
    /// <param name="map">Maps the endpoints.</param>
    /// <param name="exceptionHandler">An optional exception handler instance to register.</param>
    /// <param name="development">Whether to run in the Development environment (otherwise Production).</param>
    /// <param name="configureServices">Optional additional service registrations.</param>
    public static async Task<TestWebHost> StartAsync(
        Action<WebApplication> map,
        IApiExceptionHandler? exceptionHandler = null,
        bool development = false,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions {
            EnvironmentName = development ? Environments.Development : Environments.Production,
        });

        var logs = new CapturingLoggerProvider();

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

        if (exceptionHandler is not null)
            builder.Services.AddSingleton(exceptionHandler);

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        map(app);
        app.UseApiResponseHandling();

        await app.StartAsync();
        return new TestWebHost(app, logs);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}
