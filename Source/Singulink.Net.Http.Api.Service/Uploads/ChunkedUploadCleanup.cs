namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Cleans up chunked uploads when the application starts and every few minutes after that (see <see cref="ChunkedUploads.CleanUpAsync"/>).
/// </summary>
internal sealed class ChunkedUploadCleanup(ChunkedUploads uploads, TimeProvider timeProvider, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Singulink.Net.Http.Api.Service.Uploads");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup goes on while the first cleanup runs.
        await Task.Yield();

        using var timer = new PeriodicTimer(ChunkedUploads.CleanupInterval, timeProvider);

        try
        {
            do
            {
                try
                {
                    await uploads.CleanUpAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Chunked uploads could not be cleaned up. The next attempt is in {Interval}.", ChunkedUploads.CleanupInterval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
