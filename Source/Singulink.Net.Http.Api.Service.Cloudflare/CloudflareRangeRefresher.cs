using System.Net;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Loads Cloudflare's IP ranges at startup and refreshes them periodically. Normally they are downloaded (falling back to the saved copy if the download at
/// startup fails) and every downloaded list is saved for the next startup. With downloads turned off, the saved copy is read instead, so that changes to it
/// are picked up.
/// </summary>
internal sealed partial class CloudflareRangeRefresher : BackgroundService
{
    private static readonly Uri Ipv4RangesUri = new("https://www.cloudflare.com/ips-v4/");
    private static readonly Uri Ipv6RangesUri = new("https://www.cloudflare.com/ips-v6/");

    // Task.Delay does not accept longer delays.
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly CloudflareNetworks _networks;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CloudflareForwardingOptions _options;
    private readonly string _savedRangesPath;
    private readonly ILogger _logger;

    private bool _lastRefreshSucceeded;

    // When the ranges in use were downloaded, or when the saved copy in use was written. Only used when downloading.
    private DateTimeOffset _rangesDate;

    public CloudflareRangeRefresher(
        CloudflareNetworks networks,
        IHttpClientFactory httpClientFactory,
        IOptions<CloudflareForwardingOptions> options,
        IHostEnvironment environment,
        ILogger<CloudflareRangeRefresher> logger)
    {
        _networks = networks;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;

        // Options validation ensures the path is set.
        _savedRangesPath = Path.GetFullPath(_options.SavedRangesPath!, environment.ContentRootPath);
    }

    private TimeSpan RetryDelay
    {
        get {
            if (!_networks.IsAvailable)
                return _options.InitialRetryInterval;

            return _options.RetryInterval < _options.RefreshInterval ? _options.RetryInterval : _options.RefreshInterval;
        }
    }

    private bool RangesAreStale => DateTimeOffset.UtcNow - _rangesDate > _options.StaleWarningAge;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // In WebApplication apps, hosted services start before the server accepts requests, so the first requests already see the ranges. Startup never
        // fails because of the ranges: without any, the middleware refuses requests until they are loaded. The download timeout bounds how long startup
        // waits for a download, since hosts such as IIS stop apps that take too long to start.
        if (await TryRefreshAsync(cancellationToken) is { } exception)
        {
            if (_options.DownloadRanges)
                await UseSavedRangesAsync(exception, cancellationToken);
            else
                LogFailedRefresh(exception);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                var delay = _lastRefreshSucceeded ? _options.RefreshInterval : RetryDelay;
                await Task.Delay(delay < MaxDelay ? delay : MaxDelay, stoppingToken);

                if (await TryRefreshAsync(stoppingToken) is { } exception)
                    LogFailedRefresh(exception);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Downloads the ranges, or reads the saved copy when downloads are turned off, and puts them in use. Returns the exception that this failed with, or
    /// <see langword="null"/> if it succeeded.
    /// </summary>
    private async Task<Exception?> TryRefreshAsync(CancellationToken cancellationToken)
    {
        var exception = _options.DownloadRanges ? await TryDownloadAsync(cancellationToken) : await TryReadAsync(cancellationToken);
        _lastRefreshSucceeded = exception is null;

        return exception;
    }

    /// <summary>
    /// Downloads the ranges, puts them in use and saves them.
    /// </summary>
    private async Task<Exception?> TryDownloadAsync(CancellationToken cancellationToken)
    {
        IPNetwork[] networks;

        try
        {
            networks = await DownloadAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ex;
        }

        _networks.Set(networks);
        _rangesDate = DateTimeOffset.UtcNow;
        LogDownloaded(networks.Length);

        await SaveAsync(networks, cancellationToken);
        return null;
    }

    /// <summary>
    /// Reads the saved copy and puts it in use if it changed.
    /// </summary>
    private async Task<Exception?> TryReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (networks, savedAt) = await CloudflareRangesFile.ReadAsync(_savedRangesPath, cancellationToken);

            if (_networks.Set(networks))
                LogUsingSavedRangesOnly(networks.Length, _savedRangesPath, savedAt);

            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ex;
        }
    }

    private async Task<IPNetwork[]> DownloadAsync(CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.DownloadTimeout);

        using var client = _httpClientFactory.CreateClient(CloudflareForwardingOptions.HttpClientName);

        // The download timeout option is the only timeout that applies.
        client.Timeout = Timeout.InfiniteTimeSpan;

        var ipv4Task = client.GetStringAsync(Ipv4RangesUri, timeoutSource.Token);
        var ipv6Task = client.GetStringAsync(Ipv6RangesUri, timeoutSource.Token);
        string[] lists = await Task.WhenAll(ipv4Task, ipv6Task);

        var networks = new List<IPNetwork>();

        if (!CloudflareRangesFile.TryParseList(lists[0], networks, out int ipv4Count) || ipv4Count is 0)
            throw new InvalidDataException($"The response from {Ipv4RangesUri} is not a list of IP ranges.");

        if (!CloudflareRangesFile.TryParseList(lists[1], networks, out int ipv6Count) || ipv6Count is 0)
            throw new InvalidDataException($"The response from {Ipv6RangesUri} is not a list of IP ranges.");

        return [.. networks];
    }

    private async Task SaveAsync(IPNetwork[] networks, CancellationToken cancellationToken)
    {
        try
        {
            await CloudflareRangesFile.WriteAsync(_savedRangesPath, networks, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The downloaded ranges stay in use, so a location that cannot be written (such as a read-only deployment) never stops the service.
            LogSaveFailed(ex, _savedRangesPath);
        }
    }

    /// <summary>
    /// Puts the saved copy in use after the startup download failed, and logs what the service is running on.
    /// </summary>
    private async Task UseSavedRangesAsync(Exception downloadException, CancellationToken cancellationToken)
    {
        IPNetwork[]? networks = null;
        DateTimeOffset savedAt = default;
        Exception? readException = null;

        try
        {
            (networks, savedAt) = await CloudflareRangesFile.ReadAsync(_savedRangesPath, cancellationToken);
            _networks.Set(networks);
            _rangesDate = savedAt;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            readException = ex;
        }

        LogDownloadFailed(downloadException, RetryDelay);

        if (networks is not null)
        {
            LogUsingSavedRanges(networks.Length, _savedRangesPath, savedAt);

            if (RangesAreStale)
                LogRangesOutOfDate(_rangesDate);
        }
        else if (readException is FileNotFoundException or DirectoryNotFoundException)
        {
            LogNoSavedRanges(_savedRangesPath);
        }
        else
        {
            LogSavedRangesUnreadable(readException, _savedRangesPath);
        }
    }

    private void LogFailedRefresh(Exception exception)
    {
        if (!_options.DownloadRanges)
        {
            if (_networks.IsAvailable)
                LogReadFailed(exception, _savedRangesPath, RetryDelay);
            else
                LogReadFailedWithoutRanges(exception, _savedRangesPath, RetryDelay);
        }
        else if (!_networks.IsAvailable)
        {
            LogDownloadFailedWithoutRanges(exception, RetryDelay);
        }
        else
        {
            LogDownloadFailed(exception, RetryDelay);

            if (RangesAreStale)
                LogRangesOutOfDate(_rangesDate);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloaded {Count} Cloudflare IP ranges.")]
    private partial void LogDownloaded(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not download Cloudflare's IP ranges. Retrying in {RetryDelay}.")]
    private partial void LogDownloadFailed(Exception exception, TimeSpan retryDelay);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not download Cloudflare's IP ranges. All requests are refused until a download succeeds. Retrying in {RetryDelay}.")]
    private partial void LogDownloadFailedWithoutRanges(Exception exception, TimeSpan retryDelay);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Cloudflare's IP ranges are out of date: the ranges in use are from {RangesDate:u}. Requests through Cloudflare servers that are not in " +
            "these ranges are treated as direct connections until a download succeeds. Check that the service can reach www.cloudflare.com.")]
    private partial void LogRangesOutOfDate(DateTimeOffset rangesDate);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not save Cloudflare's IP ranges to {Path}. The downloaded ranges are used, but they are not available to the next startup.")]
    private partial void LogSaveFailed(Exception exception, string path);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Using the {Count} Cloudflare IP ranges saved at {Path} on {SavedAt:u} until a download succeeds.")]
    private partial void LogUsingSavedRanges(int count, string path, DateTimeOffset savedAt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not read the Cloudflare IP ranges saved at {Path}. All requests are refused until a download succeeds.")]
    private partial void LogSavedRangesUnreadable(Exception? exception, string path);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No saved copy of Cloudflare's IP ranges is available at {Path}. All requests are refused until a download succeeds.")]
    private partial void LogNoSavedRanges(string path);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Using the {Count} Cloudflare IP ranges saved at {Path} on {SavedAt:u}. Downloads are turned off.")]
    private partial void LogUsingSavedRangesOnly(int count, string path, DateTimeOffset savedAt);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not read Cloudflare's IP ranges from {Path}. The ranges in use are kept. Retrying in {RetryDelay}.")]
    private partial void LogReadFailed(Exception exception, string path, TimeSpan retryDelay);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not read Cloudflare's IP ranges from {Path}. All requests are refused until it can be read. Retrying in {RetryDelay}.")]
    private partial void LogReadFailedWithoutRanges(Exception exception, string path, TimeSpan retryDelay);
}
