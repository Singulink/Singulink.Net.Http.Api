using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class CloudflareRangeLoadingTests
{
    // Not in the fake handler's default lists, so trusting it proves the saved copy is in use.
    private const string SavedRangeAddress = "198.18.0.5";

    private const string DefaultRangeAddress = "173.245.48.10";
    private const string DirectAddress = "203.0.113.5";

    private const string DownloadFailedMessage = "Could not download Cloudflare's IP ranges.";
    private const string RefusedRetryMessage = "Could not download Cloudflare's IP ranges. All requests are refused until a download succeeds.";
    private const string OutOfDateMessage = "Cloudflare's IP ranges are out of date: the ranges in use are from ";
    private const string ReadFailedMessage = "Could not read Cloudflare's IP ranges from ";

    // The saved copy that tests write unless they specify other contents.
    private const string SavedContents = "198.18.0.0/15\n2001:db8:cf::/48\n# end\n";

    private static readonly string[] DefaultSavedLines = ["173.245.48.0/20", "103.21.244.0/22", "2400:cb00::/32", "2606:4700::/32", "# end"];

    private static string WriteSavedRanges(string contentRoot, string contents = SavedContents, DateTime? lastWriteTimeUtc = null)
    {
        string path = CloudflareTestHost.GetSavedRangesPath(contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc ?? DateTime.UtcNow.AddHours(-1));

        return path;
    }

    private static string FormatDate(DateTime utcDate) => new DateTimeOffset(utcDate).ToString("u", CultureInfo.InvariantCulture);

    private static string[] GetTempFiles(string savedRangesPath) => Directory.GetFiles(Path.GetDirectoryName(savedRangesPath)!, "*.tmp");

    /// <summary>
    /// Replaces the saved copy in a single step, as tools that update it should, retrying while the service has it open (which blocks replacing it on
    /// Windows).
    /// </summary>
    private static async Task ReplaceSavedRangesAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string tempPath = path + ".new";
        File.WriteAllText(tempPath, contents);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 100)
            {
                await Task.Delay(10);
            }
        }
    }

    /// <summary>
    /// Keeps the service from replacing the saved copy, which stays readable, until disposed.
    /// </summary>
    private static Restorer MakeSavedCopyUnreplaceable(string path)
    {
        // Windows refuses to replace a read-only file. Elsewhere, whether a file can be replaced depends on its directory, which also keeps the temporary
        // file from being created.
        string directory = Path.GetDirectoryName(path)!;
        var directoryMode = default(UnixFileMode);

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        else
        {
            directoryMode = File.GetUnixFileMode(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        return new(() => {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(path, FileAttributes.Normal);
            else
                File.SetUnixFileMode(directory, directoryMode);
        });
    }

    /// <summary>
    /// Waits until the service has saved the expected ranges.
    /// </summary>
    private static async Task WaitForSavedRangesAsync(string path, string[] expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string[]? lines = null;

        while (DateTime.UtcNow < deadline)
        {
            // On Windows, a file that is open cannot be replaced, so the service fails to save if this reads the file at the wrong moment. Copies
            // written by tests are at least an hour old, so this only reads the file once the service has written it.
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-10))
            {
                try
                {
                    lines = File.ReadAllLines(path);

                    if (lines.SequenceEqual(expected))
                        return;
                }
                catch (IOException)
                {
                    // The file is being replaced.
                }
            }

            await Task.Delay(20);
        }

        lines.ShouldBe(expected);
    }

    private static async Task ShouldBeRefusedAsync(HttpContext context, string retryAfter = "1")
    {
        var response = context.Response;

        response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        response.ContentType.ShouldBe("text/plain; charset=utf-8; format=error-code");
        response.Headers.RetryAfter.ToString().ShouldBe(retryAfter);
        response.Headers.CacheControl.ToString().ShouldBe("no-store");

        using var reader = new StreamReader(response.Body);
        (await reader.ReadToEndAsync()).ShouldBe("[cloudflare-ranges-unavailable] The service is starting. Please try again shortly.");
    }

    [TestMethod]
    public async Task SuccessfulDownload_SavesRanges()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        string path = host.SavedRangesPath;
        File.Exists(path).ShouldBeTrue();
        File.ReadAllLines(path).ShouldBe(DefaultSavedLines);
        GetTempFiles(path).ShouldBeEmpty();

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);
        host.WarningLogs.ShouldBeEmpty();
        host.Logs.ShouldContain(l => l.Level == LogLevel.Information && l.Message == "Downloaded 4 Cloudflare IP ranges.");
    }

    [TestMethod]
    public async Task SuccessfulDownload_ReplacesExistingSavedCopy()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        string path = WriteSavedRanges(contentRoot);

        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), contentRoot: contentRoot);

        File.ReadAllLines(path).ShouldBe(DefaultSavedLines);
        await host.WaitUntilTrustedAsync(SavedRangeAddress, trusted: false);
        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SavedRangesPath_IsResolvedAgainstContentRoot()
    {
        // The saved copy goes next to the app's folder, as the docs suggest.
        string root = CloudflareTestHost.CreateContentRoot();
        string appRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;

        try
        {
            await using (var host = await CloudflareTestHost.StartAsync(
                new FakeCloudflareHandler(),
                o => o.SavedRangesPath = "../data/cloudflare-ip-ranges.txt",
                appRoot))
            {
                File.ReadAllLines(Path.Combine(root, "data", "cloudflare-ip-ranges.txt")).ShouldBe(DefaultSavedLines);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailedDownload_UsesSavedCopyAndWarnsWithItsDate()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        var savedAt = DateTime.UtcNow.AddDays(-2);
        string path = WriteSavedRanges(contentRoot, lastWriteTimeUtc: savedAt);

        var handler = new FakeCloudflareHandler { Fail = true };
        await using var host = await CloudflareTestHost.StartAsync(handler, contentRoot: contentRoot);

        var context = await host.SendAsync(SavedRangeAddress, ("CF-Connecting-IP", "198.51.100.7"));
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("198.51.100.7"));

        context = await host.SendAsync("2001:db8:cf::1", ("CF-Connecting-IP", "198.51.100.7"));
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("198.51.100.7"));

        await host.WaitUntilTrustedAsync(DefaultRangeAddress, trusted: false);

        var warnings = host.WarningLogs;
        warnings.Count.ShouldBe(2);
        warnings.ShouldAllBe(l => l.Level == LogLevel.Warning);
        warnings[0].Message.ShouldBe(DownloadFailedMessage + " Retrying in 00:05:00.");
        warnings[0].Exception.ShouldBeOfType<HttpRequestException>();
        warnings[1].Message.ShouldBe($"Using the 2 Cloudflare IP ranges saved at {path} on {FormatDate(savedAt)} until a download succeeds.");

        // The saved copy is left as it was.
        File.ReadAllText(path).ShouldBe(SavedContents);
    }

    [TestMethod]
    public async Task FailedDownload_WithOutOfDateSavedCopy_WarnsThatItIsOutOfDate()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        var savedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        WriteSavedRanges(contentRoot, lastWriteTimeUtc: savedAt);

        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler { Fail = true }, contentRoot: contentRoot);

        await host.WaitUntilTrustedAsync(SavedRangeAddress);

        var warnings = host.WarningLogs;
        warnings.Count.ShouldBe(3);
        warnings[1].Message.ShouldContain("on 2026-01-02 03:04:05Z until a download succeeds.");
        warnings[2].Level.ShouldBe(LogLevel.Warning);
        warnings[2].Message.ShouldStartWith(OutOfDateMessage + "2026-01-02 03:04:05Z.");
    }

    [TestMethod]
    public async Task FirstStart_RefusesRequestsUntilDownloadSucceeds()
    {
        var handler = new FakeCloudflareHandler { Fail = true };
        await using var host = await CloudflareTestHost.StartAsync(handler, o => o.InitialRetryInterval = TimeSpan.FromMilliseconds(50));

        // Neither requests through Cloudflare nor direct requests are served, since they cannot be told apart.
        await ShouldBeRefusedAsync(await host.SendAsync(DefaultRangeAddress, ("CF-Connecting-IP", "198.51.100.7")));
        await ShouldBeRefusedAsync(await host.SendAsync(DirectAddress));

        var warnings = host.WarningLogs;
        warnings[0].Message.ShouldBe(DownloadFailedMessage + " Retrying in 00:00:00.0500000.");
        warnings[1].Level.ShouldBe(LogLevel.Warning);
        warnings[1].Message.ShouldBe(
            $"No saved copy of Cloudflare's IP ranges is available at {host.SavedRangesPath}. All requests are refused until a download succeeds.");

        // Each failed retry is logged.
        var retryWarning = await host.WaitForLogAsync(l => l.Message.StartsWith(RefusedRetryMessage, StringComparison.Ordinal));
        retryWarning.Level.ShouldBe(LogLevel.Warning);
        retryWarning.Message.ShouldEndWith("Retrying in 00:00:00.0500000.");
        retryWarning.Exception.ShouldBeOfType<HttpRequestException>();

        await ShouldBeRefusedAsync(await host.SendAsync(DirectAddress));

        handler.Fail = false;

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);

        var context = await host.SendAsync(DirectAddress, ("CF-IPCountry", "CA"));
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Request.Headers.ContainsKey("CF-IPCountry").ShouldBeFalse();

        await WaitForSavedRangesAsync(host.SavedRangesPath, DefaultSavedLines);
    }

    [TestMethod]
    public async Task FirstStart_RetryAfterIsInitialRetryIntervalInWholeSeconds()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler { Fail = true });
        await ShouldBeRefusedAsync(await host.SendAsync(DirectAddress), retryAfter: "10");

        await using var host2 = await CloudflareTestHost.StartAsync(
            new FakeCloudflareHandler { Fail = true },
            o => o.InitialRetryInterval = TimeSpan.FromMilliseconds(1500));

        await ShouldBeRefusedAsync(await host2.SendAsync(DirectAddress), retryAfter: "2");
    }

    [TestMethod]
    public async Task FirstStart_HangingDownload_DoesNotBlockStartup()
    {
        var stopwatch = Stopwatch.StartNew();

        await using var host = await CloudflareTestHost.StartAsync(
            new FakeCloudflareHandler { Hang = true },
            o => o.DownloadTimeout = TimeSpan.FromMilliseconds(200));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        await ShouldBeRefusedAsync(await host.SendAsync(DefaultRangeAddress), retryAfter: "10");
        host.WarningLogs[0].Exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [TestMethod]
    [DataRow("198.18.0.0/15\nnot a range\n# end\n")]
    [DataRow("")]
    [DataRow("# Only a comment\n\n")]
    [DataRow("# end\n")]
    [DataRow("198.18.0.0/15\n2001:db8:cf::/48\n")]
    [DataRow("198.18.0.0/15\n2001:db8:cf::/48\n# en")]
    [DataRow("198.18.0.0/15\n# end\n2001:db8:cf::/48\n")]
    [DataRow("198.18.0.0/15\n\0\0\0\0\0\0\n# end\n")]
    public async Task FailedDownload_WithInvalidSavedCopy_RefusesRequestsUntilDownloadSucceeds(string contents)
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        string path = WriteSavedRanges(contentRoot, contents);

        var handler = new FakeCloudflareHandler { Fail = true };
        await using var host = await CloudflareTestHost.StartAsync(handler, o => o.InitialRetryInterval = TimeSpan.FromMilliseconds(50), contentRoot);

        await ShouldBeRefusedAsync(await host.SendAsync(SavedRangeAddress, ("CF-Connecting-IP", "198.51.100.7")));

        var log = host.WarningLogs[1];
        log.Level.ShouldBe(LogLevel.Error);
        log.Message.ShouldBe($"Could not read the Cloudflare IP ranges saved at {path}. All requests are refused until a download succeeds.");
        log.Exception.ShouldBeOfType<InvalidDataException>();

        // Retries use the initial retry interval, and the next download replaces the invalid copy.
        host.WarningLogs[0].Message.ShouldEndWith("Retrying in 00:00:00.0500000.");
        handler.Fail = false;

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);
        await WaitForSavedRangesAsync(path, DefaultSavedLines);
    }

    [TestMethod]
    public async Task Save_WaitsBrieflyForOtherReadersOfTheSavedCopy()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        string path = WriteSavedRanges(contentRoot);

        // Another process reading the file keeps it from being replaced on Windows.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task<CloudflareTestHost> startTask;

        try
        {
            startTask = CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), contentRoot: contentRoot);
            await Task.Delay(300);
        }
        finally
        {
            reader.Dispose();
        }

        await using var host = await startTask;

        File.ReadAllLines(path).ShouldBe(DefaultSavedLines);
        GetTempFiles(path).ShouldBeEmpty();
        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task UnwritableSaveLocation_UsesDownloadedRanges()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();

        // A file where the directory should be keeps the directory from being created.
        File.WriteAllText(Path.Combine(contentRoot, "blocked"), string.Empty);

        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => o.SavedRangesPath = "blocked/ranges.txt", contentRoot);

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);

        var log = host.WarningLogs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Warning);
        log.Message.ShouldStartWith($"Could not save Cloudflare's IP ranges to {Path.GetFullPath("blocked/ranges.txt", contentRoot)}.");
        log.Exception.ShouldNotBeNull();
    }

    [TestMethod]
    public async Task UnwritableSeededCopy_IsUsedAndRefreshedInMemory()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        string path = WriteSavedRanges(contentRoot);

        var handler = new FakeCloudflareHandler { Fail = true };

        await using var host = await CloudflareTestHost.StartAsync(handler, o => o.RetryInterval = TimeSpan.FromMilliseconds(50), contentRoot);

        // Stands in for a read-only location: the file can be read, but replacing it fails. Nothing is saved before this, since downloads fail until later,
        // and it is restored before the host deletes its content root.
        using var unreplaceable = MakeSavedCopyUnreplaceable(path);

        await host.WaitUntilTrustedAsync(SavedRangeAddress);

        handler.Fail = false;

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);
        await host.WaitUntilTrustedAsync(SavedRangeAddress, trusted: false);

        var log = await host.WaitForLogAsync(l => l.Message.StartsWith("Could not save Cloudflare's IP ranges", StringComparison.Ordinal));
        log.Level.ShouldBe(LogLevel.Warning);

        File.ReadAllText(path).ShouldBe(SavedContents);
        GetTempFiles(path).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task InvalidDownloadedList_IsRejected()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        WriteSavedRanges(contentRoot);

        var handler = new FakeCloudflareHandler { Ipv6Ranges = "<html>\n2400:cb00::/32\n</html>\n" };
        await using var host = await CloudflareTestHost.StartAsync(handler, contentRoot: contentRoot);

        await host.WaitUntilTrustedAsync(SavedRangeAddress);
        await host.WaitUntilTrustedAsync(DefaultRangeAddress, trusted: false);
        host.WarningLogs[0].Exception.ShouldBeOfType<InvalidDataException>();
    }

    [TestMethod]
    public async Task EmptyDownloadedList_IsRejected()
    {
        var handler = new FakeCloudflareHandler { Ipv4Ranges = "# nothing here\n" };
        await using var host = await CloudflareTestHost.StartAsync(handler);

        await ShouldBeRefusedAsync(await host.SendAsync("2400:cb00::1"), retryAfter: "10");
        host.WarningLogs[0].Exception.ShouldBeOfType<InvalidDataException>();
        File.Exists(host.SavedRangesPath).ShouldBeFalse();
    }

    [TestMethod]
    public async Task HangingDownload_TimesOutWithoutBlockingStartup()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        WriteSavedRanges(contentRoot);

        var stopwatch = Stopwatch.StartNew();

        await using var host = await CloudflareTestHost.StartAsync(
            new FakeCloudflareHandler { Hang = true },
            o => o.DownloadTimeout = TimeSpan.FromMilliseconds(200),
            contentRoot);

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        await host.WaitUntilTrustedAsync(SavedRangeAddress);
        host.WarningLogs[0].Message.ShouldStartWith(DownloadFailedMessage);
    }

    [TestMethod]
    public async Task Refresh_ReplacesRanges()
    {
        var handler = new FakeCloudflareHandler();

        await using var host = await CloudflareTestHost.StartAsync(handler, o => o.RefreshInterval = TimeSpan.FromMilliseconds(50));

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);

        handler.Ipv4Ranges = "198.18.0.0/15\n";

        await host.WaitUntilTrustedAsync(SavedRangeAddress);
        await host.WaitUntilTrustedAsync(DefaultRangeAddress, trusted: false);
        await host.WaitUntilTrustedAsync("2400:cb00::1");

        // The saved copy follows the refreshed ranges (it is written after they are applied).
        await WaitForSavedRangesAsync(host.SavedRangesPath, ["198.18.0.0/15", "2400:cb00::/32", "2606:4700::/32", "# end"]);
    }

    [TestMethod]
    public async Task FailedRefresh_KeepsCurrentRanges()
    {
        var handler = new FakeCloudflareHandler();

        await using var host = await CloudflareTestHost.StartAsync(handler, o => {
            o.RefreshInterval = TimeSpan.FromMilliseconds(50);
            o.RetryInterval = TimeSpan.FromMilliseconds(50);
        });

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);

        handler.Fail = true;
        int requestCount = handler.RequestCount;

        while (handler.RequestCount < requestCount + 4)
            await Task.Delay(20);

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);

        // The ranges are not out of date yet, so each failure logs a single warning.
        var warnings = host.WarningLogs;
        warnings.ShouldNotBeEmpty();
        warnings.ShouldAllBe(l => l.Level == LogLevel.Warning && l.Message == DownloadFailedMessage + " Retrying in 00:00:00.0500000.");
    }

    [TestMethod]
    public async Task FailedRefresh_WithOutOfDateRanges_WarnsThatTheyAreOutOfDate()
    {
        var handler = new FakeCloudflareHandler();
        var startedAt = DateTimeOffset.UtcNow;

        await using var host = await CloudflareTestHost.StartAsync(handler, o => {
            o.RefreshInterval = TimeSpan.FromMilliseconds(50);
            o.RetryInterval = TimeSpan.FromMilliseconds(50);
            o.StaleWarningAge = TimeSpan.FromMilliseconds(1);
        });

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);

        // Successful refreshes never warn.
        host.WarningLogs.ShouldBeEmpty();

        handler.Fail = true;

        // It is logged on each failed attempt.
        static bool IsOutOfDateWarning(LogEntry l) => l.Message.StartsWith(OutOfDateMessage, StringComparison.Ordinal);
        var log = await host.WaitForLogAsync(IsOutOfDateWarning, count: 2);
        var failedAt = DateTimeOffset.UtcNow;

        log.Level.ShouldBe(LogLevel.Warning);

        // Each out of date warning follows the failed download's warning.
        var warnings = host.WarningLogs.ToList();
        int index = warnings.FindIndex(IsOutOfDateWarning);
        index.ShouldBeGreaterThan(0);
        warnings[index - 1].Message.ShouldStartWith(DownloadFailedMessage);

        // The warning reports when the ranges in use were downloaded (in whole seconds).
        string date = log.Message[OutOfDateMessage.Length..log.Message.IndexOf("Z.", StringComparison.Ordinal)];
        var rangesDate = DateTimeOffset.ParseExact(date + "Z", "u", CultureInfo.InvariantCulture);
        rangesDate.ShouldBeGreaterThan(startedAt.AddSeconds(-1));
        rangesDate.ShouldBeLessThanOrEqualTo(failedAt);
    }

    [TestMethod]
    public async Task FailedStartupDownload_WithoutSavedCopy_IsRetriedAfterInitialRetryInterval()
    {
        var handler = new FakeCloudflareHandler { Fail = true };

        await using var host = await CloudflareTestHost.StartAsync(handler, o => {
            o.InitialRetryInterval = TimeSpan.FromMilliseconds(50);
            o.RetryInterval = TimeSpan.FromMinutes(1);
        });

        host.WarningLogs[0].Message.ShouldContain("Retrying in 00:00:00.05");

        handler.Fail = false;

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);

        // The download is saved after it is put in use. The test ends once it is saved, rather than while it is being saved.
        await WaitForSavedRangesAsync(CloudflareTestHost.GetSavedRangesPath(host.ContentRoot), DefaultSavedLines);
    }

    [TestMethod]
    public async Task FailedStartupDownload_WithSavedCopy_IsRetriedAfterRetryInterval()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        WriteSavedRanges(contentRoot);

        var handler = new FakeCloudflareHandler { Fail = true };

        await using var host = await CloudflareTestHost.StartAsync(
            handler,
            o => {
                o.InitialRetryInterval = TimeSpan.FromMinutes(1);
                o.RetryInterval = TimeSpan.FromMilliseconds(50);
            },
            contentRoot);

        host.WarningLogs[0].Message.ShouldContain("Retrying in 00:00:00.05");

        handler.Fail = false;

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);
        await WaitForSavedRangesAsync(CloudflareTestHost.GetSavedRangesPath(contentRoot), DefaultSavedLines);
    }

    [TestMethod]
    [DataRow("198.18.0.0/15\r\n2001:db8:cf::/48\r\n# end\r\n")]
    [DataRow("# Cloudflare's ranges\n198.18.0.0/15\n\n2001:db8:cf::/48\n  # end  \n\n\n")]
    [DataRow("﻿198.18.0.0/15\n2001:db8:cf::/48\n# end")]
    public async Task SavedCopy_AllowsLineEndingsCommentsAndBlankLines(string contents)
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        WriteSavedRanges(contentRoot, contents);

        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => o.DownloadRanges = false, contentRoot);

        await host.WaitUntilTrustedAsync(SavedRangeAddress);
        await host.WaitUntilTrustedAsync("2001:db8:cf::1");
        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SavedCopy_CanBeUtf16()
    {
        // Windows PowerShell 5.1 writes UTF-16 files with ">" and Out-File.
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        string path = WriteSavedRanges(contentRoot);
        File.WriteAllText(path, SavedContents, Encoding.Unicode);

        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => o.DownloadRanges = false, contentRoot);

        await host.WaitUntilTrustedAsync(SavedRangeAddress);
        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DownloadsTurnedOff_UsesSavedCopyWithoutDownloading()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();

        // Out of date, which is not reported: keeping the copy current is up to the app when downloads are turned off.
        string path = WriteSavedRanges(contentRoot, lastWriteTimeUtc: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var handler = new FakeCloudflareHandler();

        await using var host = await CloudflareTestHost.StartAsync(
            handler,
            o => {
                o.DownloadRanges = false;
                o.RefreshInterval = TimeSpan.FromMilliseconds(50);
                o.StaleWarningAge = TimeSpan.FromMilliseconds(1);
            },
            contentRoot);

        // In use as soon as startup completes.
        var context = await host.SendAsync(SavedRangeAddress, ("CF-Connecting-IP", "198.51.100.7"));
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("198.51.100.7"));

        // The file is read again several times, but nothing is downloaded, and an unchanged file is not logged again.
        await Task.Delay(300);
        handler.RequestCount.ShouldBe(0);
        host.WarningLogs.ShouldBeEmpty();

        var log = host.Logs.Where(l => l.Message.StartsWith("Using the ", StringComparison.Ordinal)).ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Information);
        log.Message.ShouldBe($"Using the 2 Cloudflare IP ranges saved at {path} on 2026-01-02 03:04:05Z. Downloads are turned off.");

        File.ReadAllText(path).ShouldBe(SavedContents);
        GetTempFiles(path).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DownloadsTurnedOff_PicksUpChangesToSavedCopy()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        string path = WriteSavedRanges(contentRoot);

        var handler = new FakeCloudflareHandler();

        await using var host = await CloudflareTestHost.StartAsync(
            handler,
            o => {
                o.DownloadRanges = false;
                o.RefreshInterval = TimeSpan.FromMilliseconds(50);
            },
            contentRoot);

        await host.WaitUntilTrustedAsync(SavedRangeAddress);

        await ReplaceSavedRangesAsync(path, "173.245.48.0/20\n# end\n");

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);
        await host.WaitUntilTrustedAsync(SavedRangeAddress, trusted: false);

        var log = await host.WaitForLogAsync(l => l.Message.StartsWith($"Using the 1 Cloudflare IP ranges saved at {path} on ", StringComparison.Ordinal));
        log.Level.ShouldBe(LogLevel.Information);
        handler.RequestCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task DownloadsTurnedOff_IncompleteSavedCopy_KeepsRangesInUse()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        string path = WriteSavedRanges(contentRoot);

        await using var host = await CloudflareTestHost.StartAsync(
            new FakeCloudflareHandler(),
            o => {
                o.DownloadRanges = false;
                o.RefreshInterval = TimeSpan.FromMilliseconds(50);
                o.RetryInterval = TimeSpan.FromMilliseconds(50);
            },
            contentRoot);

        // A file that is read while it is still being written lacks its end line.
        await ReplaceSavedRangesAsync(path, "173.245.48.0/20\n2400:cb00::/32\n");

        var log = await host.WaitForLogAsync(l => l.Message.StartsWith(ReadFailedMessage, StringComparison.Ordinal));
        log.Level.ShouldBe(LogLevel.Warning);
        log.Message.ShouldBe($"{ReadFailedMessage}{path}. The ranges in use are kept. Retrying in 00:00:00.0500000.");
        log.Exception.ShouldBeOfType<InvalidDataException>().Message.ShouldBe("The file does not end with a \"# end\" line, so it may be incomplete.");

        await host.WaitUntilTrustedAsync(SavedRangeAddress);
        await host.WaitUntilTrustedAsync(DefaultRangeAddress, trusted: false);

        // Once it is complete, it is used.
        await ReplaceSavedRangesAsync(path, "173.245.48.0/20\n2400:cb00::/32\n# end\n");

        await host.WaitUntilTrustedAsync(DefaultRangeAddress);
        await host.WaitUntilTrustedAsync(SavedRangeAddress, trusted: false);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("198.18.0.0/15\nnot a range\n# end\n")]
    [DataRow("198.18.0.0/15\n2001:db8:cf::/48\n")]
    public async Task DownloadsTurnedOff_WithoutUsableSavedCopy_RefusesRequestsUntilItCanBeRead(string? contents)
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        string path = contents is null ? CloudflareTestHost.GetSavedRangesPath(contentRoot) : WriteSavedRanges(contentRoot, contents);

        var handler = new FakeCloudflareHandler();

        await using var host = await CloudflareTestHost.StartAsync(
            handler,
            o => {
                o.DownloadRanges = false;
                o.InitialRetryInterval = TimeSpan.FromMilliseconds(50);
            },
            contentRoot);

        // Neither requests through Cloudflare nor direct requests are served, since they cannot be told apart.
        await ShouldBeRefusedAsync(await host.SendAsync(SavedRangeAddress, ("CF-Connecting-IP", "198.51.100.7")));
        await ShouldBeRefusedAsync(await host.SendAsync(DirectAddress));

        var log = host.WarningLogs[0];
        log.Level.ShouldBe(LogLevel.Warning);
        log.Message.ShouldBe($"{ReadFailedMessage}{path}. All requests are refused until it can be read. Retrying in 00:00:00.0500000.");

        // A missing file or folder, or a file that is not a complete list.
        if (contents is null)
            log.Exception.ShouldBeAssignableTo<IOException>();
        else
            log.Exception.ShouldBeOfType<InvalidDataException>();

        // Each failed retry is logged.
        await host.WaitForLogAsync(l => l.Message.StartsWith(ReadFailedMessage, StringComparison.Ordinal), count: 2);

        await ReplaceSavedRangesAsync(path, SavedContents);

        await host.WaitUntilTrustedAsync(SavedRangeAddress);
        handler.RequestCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task RetryInterval_IsLimitedToRefreshInterval()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        WriteSavedRanges(contentRoot);

        await using var host = await CloudflareTestHost.StartAsync(
            new FakeCloudflareHandler { Fail = true },
            o => o.RefreshInterval = TimeSpan.FromMinutes(1),
            contentRoot);

        host.WarningLogs[0].Message.ShouldContain("Retrying in 00:01:00");
    }

    private sealed class Restorer(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
