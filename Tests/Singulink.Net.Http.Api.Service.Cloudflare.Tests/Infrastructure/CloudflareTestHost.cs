using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// An in-process web application (using the test server) with Cloudflare forwarding applied first in the pipeline and Cloudflare's range lists served by a
/// <see cref="FakeCloudflareHandler"/>. Each host gets its own temporary content root directory, deleted on disposal.
/// </summary>
public sealed class CloudflareTestHost : IAsyncDisposable
{
    /// <summary>
    /// The saved ranges path that hosts use unless the options configuration changes it, relative to the content root.
    /// </summary>
    public const string SavedRangesFile = "data/cloudflare-ip-ranges.txt";

    private readonly CapturingLoggerProvider _logs;

    private CloudflareTestHost(WebApplication app, CapturingLoggerProvider logs, string contentRoot)
    {
        App = app;
        _logs = logs;
        ContentRoot = contentRoot;
    }

    public WebApplication App { get; }

    public string ContentRoot { get; }

    /// <summary>
    /// Gets the full path of <see cref="SavedRangesFile"/> in this host's content root.
    /// </summary>
    public string SavedRangesPath => GetSavedRangesPath(ContentRoot);

    public IReadOnlyList<LogEntry> Logs => _logs.Entries;

    /// <summary>
    /// Gets the warnings and errors logged so far, except the middleware's warning about a direct connection that forwards another address, which the
    /// tests' requests from untrusted addresses cause.
    /// </summary>
    public IReadOnlyList<LogEntry> WarningLogs => _logs.Entries
        .Where(l => l.Level >= LogLevel.Warning && !l.Category.EndsWith(".CloudflareForwardingMiddleware", StringComparison.Ordinal))
        .ToList();

    /// <summary>
    /// Creates a new temporary directory to use as a content root.
    /// </summary>
    public static string CreateContentRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "Singulink.Net.Http.Api.Service.Cloudflare.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Gets the full path of <see cref="SavedRangesFile"/> in the specified content root.
    /// </summary>
    public static string GetSavedRangesPath(string contentRoot) => Path.GetFullPath(SavedRangesFile, contentRoot);

    /// <summary>
    /// Starts a web application with Cloudflare forwarding. <see cref="CloudflareForwardingOptions.SavedRangesPath"/> is set to <see
    /// cref="SavedRangesFile"/> before <paramref name="configure"/> runs.
    /// </summary>
    /// <param name="handler">Serves the range lists.</param>
    /// <param name="configure">Optional forwarding options configuration.</param>
    /// <param name="contentRoot">The content root to use (see <see cref="CreateContentRoot"/>), or <see langword="null"/> to create one.</param>
    /// <param name="configureServices">Optional additional service registrations, made before the Cloudflare forwarding services are registered.</param>
    public static async Task<CloudflareTestHost> StartAsync(
        FakeCloudflareHandler handler,
        Action<CloudflareForwardingOptions>? configure = null,
        string? contentRoot = null,
        Action<IServiceCollection>? configureServices = null)
    {
        contentRoot ??= CreateContentRoot();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions {
            EnvironmentName = Environments.Production,
            ContentRootPath = contentRoot,
        });

        var logs = new CapturingLoggerProvider();

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        configureServices?.Invoke(builder.Services);

        builder.Services.AddCloudflareForwarding(o => {
            o.SavedRangesPath = SavedRangesFile;
            configure?.Invoke(o);
        });

        builder.Services.AddHttpClient(CloudflareForwardingOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

        var app = builder.Build();
        app.UseCloudflareForwarding();
        app.Run(_ => Task.CompletedTask);

        var host = new CloudflareTestHost(app, logs, contentRoot);

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        return host;
    }

    /// <summary>
    /// Sends a request from the specified remote address with the specified headers and returns the server's context after the pipeline has run.
    /// </summary>
    public Task<HttpContext> SendAsync(string remoteAddress, params (string Name, string Value)[] headers)
    {
        return App.GetTestServer().SendAsync(context => {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

            foreach (var (name, value) in headers)
                context.Request.Headers.Append(name, value);
        });
    }

    /// <summary>
    /// Sends requests from the specified address until a request is treated as coming through Cloudflare (or not), failing after a timeout.
    /// </summary>
    public async Task WaitUntilTrustedAsync(string remoteAddress, bool trusted = true)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            var context = await SendAsync(remoteAddress, ("CF-Connecting-IP", "198.51.100.1"));

            if (context.Connection.RemoteIpAddress!.Equals(IPAddress.Parse("198.51.100.1")) == trusted)
                return;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Address {remoteAddress} did not become {(trusted ? "trusted" : "untrusted")}.");

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Waits until at least the specified number of log entries match the specified condition and returns the first one, failing after a timeout.
    /// </summary>
    public async Task<LogEntry> WaitForLogAsync(Func<LogEntry, bool> predicate, int count = 1)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            var entries = Logs.Where(predicate).ToList();

            if (entries.Count >= count)
                return entries[0];

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {count} matching log entries but found {entries.Count}.");

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();

        // On Windows, a file that another process (such as a virus scanner) still has open keeps its directory from being deleted for a moment, with an
        // IOException or, for a file that is open without delete sharing, an UnauthorizedAccessException. Tests whose last download succeeds just before
        // they end save the ranges right then. A directory that cannot be deleted is left behind rather than failing the test.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(ContentRoot, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                await Task.Delay(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
