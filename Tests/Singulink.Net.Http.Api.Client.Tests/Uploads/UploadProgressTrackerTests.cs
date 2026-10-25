namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class UploadProgressTrackerTests
{
    private const int KiB = 1024;

    [TestMethod]
    public void Attempts_CountWrittenBytesUntilTheyEnd()
    {
        var progress = new RecordingProgress<UploadProgress>();
        var tracker = new UploadProgressTracker(progress, 1000);

        var first = tracker.StartAttempt();
        var second = tracker.StartAttempt();

        first.OnWritten(100);
        second.OnWritten(50);
        first.End(300);
        second.OnWritten(50);
        second.End(0);
        second.OnWritten(10);

        progress.Reports.Select(p => p.BytesUploaded).ShouldBe([100, 150, 350, 400, 300]);
        progress.Reports.ShouldAllBe(p => p.Length == 1000 && !p.IsProcessing);
    }

    [TestMethod]
    public void RestartedAttempt_NoLongerCountsItsBytes()
    {
        var progress = new RecordingProgress<UploadProgress>();
        var tracker = new UploadProgressTracker(progress, 1000);
        var attempt = tracker.StartAttempt();

        attempt.OnWritten(200);
        attempt.OnRestarted();
        attempt.OnWritten(200);
        attempt.End(300);

        progress.Reports.Select(p => p.BytesUploaded).ShouldBe([200, 0, 200, 300]);
    }

    [TestMethod]
    public async Task ReportThatTakesLong_DoesNotHoldUpOtherThreads_WhoseProgressIsReportedNext()
    {
        var progress = new BlockingProgress();
        var tracker = new UploadProgressTracker(progress, 1000);
        var first = tracker.StartAttempt();
        var second = tracker.StartAttempt();

        var reporting = Task.Run(() => first.End(400));
        await WithinTimeout(progress.FirstReportStarted);

        // Another thread changes the progress while the first report is made.
        await WithinTimeout(Task.Run(() => second.End(600)));

        progress.Release();
        await WithinTimeout(reporting);

        progress.Reports.Select(p => p.BytesUploaded).ShouldBe([400, 1000]);
    }

    [TestMethod]
    public void UnchangedProgress_IsNotReportedAgain()
    {
        var progress = new RecordingProgress<UploadProgress>();
        var tracker = new UploadProgressTracker(progress, 1000);
        var attempt = tracker.StartAttempt();

        attempt.OnRestarted();
        attempt.OnWritten(300);
        attempt.End(300);
        tracker.SetAcknowledged(300);
        tracker.SetProcessing();
        tracker.SetProcessing();

        progress.Reports.ShouldBe([new UploadProgress(300, 1000, false), new UploadProgress(300, 1000, true)]);
    }

    [TestMethod]
    public void SetAcknowledged_ReplacesTheAcknowledgedBytesAndEndsProcessing()
    {
        var progress = new RecordingProgress<UploadProgress>();
        var tracker = new UploadProgressTracker(progress, 1000);

        tracker.SetAcknowledged(1000);
        tracker.SetProcessing();
        tracker.SetAcknowledged(700);

        progress.Reports.ShouldBe([new(1000, 1000, false), new(1000, 1000, true), new(700, 1000, false)]);
    }

    [TestMethod]
    public void NoProgress_TracksWithoutReporting()
    {
        var tracker = new UploadProgressTracker(null, 1000);

        tracker.StartAttempt().End(100);
        tracker.SetProcessing();
    }

    [TestMethod]
    public async Task ChunkContent_WritesTheBytesInSlicesOf64KiB()
    {
        var progress = new RecordingProgress<UploadProgress>();
        var tracker = new UploadProgressTracker(progress, 1000 * KiB);
        byte[] buffer = [.. Enumerable.Range(0, 200 * KiB).Select(i => (byte)i), 1, 2, 3];
        using var inactivity = new InactivityTimeout(Timeout.InfiniteTimeSpan, TimeProvider.System, CancellationToken.None);
        using var content = new ChunkContent(buffer, 200 * KiB, tracker.StartAttempt(), inactivity);

        content.Headers.ContentLength.ShouldBe(200 * KiB);

        byte[] written = await content.ReadAsByteArrayAsync();

        written.ShouldBe(buffer.AsSpan(0, 200 * KiB).ToArray());
        progress.Reports.Select(p => p.BytesUploaded).ShouldBe([64 * KiB, 128 * KiB, 192 * KiB, 200 * KiB]);
    }

    [TestMethod]
    public async Task ChunkContent_WrittenAgain_CountsItsBytesOnce()
    {
        var progress = new RecordingProgress<UploadProgress>();
        var tracker = new UploadProgressTracker(progress, 1000 * KiB);
        using var inactivity = new InactivityTimeout(Timeout.InfiniteTimeSpan, TimeProvider.System, CancellationToken.None);
        using var content = new ChunkContent(new byte[100 * KiB], 100 * KiB, tracker.StartAttempt(), inactivity);

        await content.CopyToAsync(new MemoryStream());
        await content.CopyToAsync(new MemoryStream());

        progress.Reports.Select(p => p.BytesUploaded).ShouldBe([64 * KiB, 100 * KiB, 0, 64 * KiB, 100 * KiB]);
    }

    private static async Task WithinTimeout(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task)
            throw new ShouldAssertException("The task did not complete in time.");

        await task;
    }

    /// <summary>
    /// A progress whose first report waits until it is released.
    /// </summary>
    private sealed class BlockingProgress : IProgress<UploadProgress>
    {
        private readonly TaskCompletionSource<bool> _firstReportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _released = new();
        private readonly List<UploadProgress> _reports = [];

        public Task FirstReportStarted => _firstReportStarted.Task;

        public List<UploadProgress> Reports
        {
            get {
                lock (_reports)
                    return [.. _reports];
            }
        }

        public void Release() => _released.Set();

        public void Report(UploadProgress value)
        {
            lock (_reports)
                _reports.Add(value);

            if (_firstReportStarted.TrySetResult(true))
                _released.Wait(TimeSpan.FromSeconds(10));
        }
    }
}
