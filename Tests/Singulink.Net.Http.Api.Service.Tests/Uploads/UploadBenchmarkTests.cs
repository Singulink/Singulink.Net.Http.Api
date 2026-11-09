using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Measures the cost of creating an upload, of whole uploads of small files and of the store's listing, cleanup and removal of outcomes, with the
/// finished and the open uploads that a user has in the directory store, and the uploads a second of one user and of two, which measure the machine as
/// much as the library: it runs in parallel with nothing else, but a virus scanner can take most of the CPU. Runs only when the
/// <c>HTTPAPI_BENCHMARKS</c> environment variable is <c>1</c>, so not in CI, and adds its numbers to the file that <c>HTTPAPI_BENCHMARK_OUTPUT</c> names,
/// if it is set, and to the test's output.
/// </summary>
[PrefixTestClass]
[DoNotParallelize]
public sealed class UploadBenchmarkTests
{
    private const string Pattern = "/folders/{folderId}/files";
    private const string UserId = "1";
    private const int Creations = 100;
    private const int Uploads = 30;
    private const int FileLength = 100 * 1024;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Benchmark]
    public async Task Creation_WithFinishedUploadsInTheStore()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => Task.FromResult(new Note("ok"))));
        var store = host.Services.GetRequiredService<IUploadStore>();
        var report = new StringBuilder();
        int seeded = 0;
        List<string> finishedIds = [];

        // The test server has no network, so the direct upload saves no round trips here.
        report.AppendLine(
            "Finished uploads | creation median / p90 / mean (ms) | small file, 3 requests median (ms) | small file, direct, median (ms) | " +
            "store listing of all (ms, uploads listed) | store cleanup (ms) | read of a finished upload median (ms)");

        foreach (int finished in new[] { 0, 1_000, 10_000 })
        {
            finishedIds.AddRange(await SeedAsync(store, finished - seeded));
            seeded = finished;

            var creations = await TimeCreationsAsync(host);
            var chunked = await TimeChunkedUploadsAsync(host);
            var direct = await TimeDirectUploadsAsync(host);
            var (listing, listed) = await TimeListingAsync(store);
            var cleanup = await TimeAsync(() => store.CleanUpAsync(TestContext.CancellationToken).AsTask());

            string directTime = direct is null ? "n/a" : Median(direct).ToString("F2", CultureInfo.InvariantCulture);
            string creationTimes = Invariant($"{Median(creations):F2} / {Percentile(creations, 0.9):F2} / {creations.Average():F2}");

            var reads = await TimeReadsAsync(store, finishedIds);
            string readTime = reads is null ? "n/a" : Median(reads).ToString("F3", CultureInfo.InvariantCulture);
            string storeTimes = Invariant($"{listing:F1} ({listed:N0}) | {cleanup:F1} | {readTime}");

            report.AppendLine(Invariant($"{finished,6:N0} | {creationTimes} | {Median(chunked):F2} | {directTime} | {storeTimes}"));
        }

        await WriteReportAsync(report);
    }

    [TestMethod]
    [Benchmark]
    public async Task Creation_WithOpenUploadsOfTheUser()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => Task.FromResult(new Note("ok"))), options => options.MaxUploadsPerUser = 100_000);

        var report = new StringBuilder();
        int open = 0;

        report.AppendLine("Open uploads of the user | creation median / p90 (ms) | 32 creations at a time: creations a second");

        foreach (int target in new[] { 0, 20, 200 })
        {
            // Open uploads that wait for their chunks, half of them with the chunk received.
            for (; open < target; open++)
            {
                var session = await host.CreateUploadAsync("photo.jpg", FileLength);

                if (open % 2 is 0)
                    (await host.PutChunkAsync(session, UploadTestHost.CreateFile(FileLength), 0)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }

            var creations = await TimeCreationsAsync(host);
            double perSecond = await TimeConcurrentCreationsAsync(host);

            report.AppendLine(Invariant($"{open,4} | {Median(creations):F2} / {Percentile(creations, 0.9):F2} | {perSecond:F0}"));
        }

        await WriteReportAsync(report);
    }

    [TestMethod]
    [Benchmark]
    public async Task Throughput_OfOneUserAndOfTwo()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => Task.FromResult(new Note("ok"))),
            options => {
                options.MaxUploadsPerUser = 100_000;
                options.MaxChunkSize = 32 << 20;
            },
            kestrel: true);

        var report = new StringBuilder();
        report.AppendLine("Direct uploads over Kestrel, 32 at a time for 10 seconds | uploads a second | median / p90 (ms)");
        report.AppendLine("These are limits of this machine: its CPU, its disk and a virus scanner that scans the store's files, rather than of the library.");

        foreach (var (length, users) in new[] { (100_000, 1), (3_000_000, 1), (100_000, 2) })
        {
            var (perSecond, times) = await TimeDirectThroughputAsync(host, UploadTestHost.CreateFile(length), users);
            string load = Invariant($"{length / 1000} KB, {users} {(users is 1 ? "user" : "users")}");
            report.AppendLine(Invariant($"{load} | {perSecond:F0} | {Median(times):F0} / {Percentile(times, 0.9):F0}"));
        }

        await WriteReportAsync(report);
    }

    [TestMethod]
    [Benchmark]
    public async Task OutcomeRemoval_WithFinishedUploadsInTheStore()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => Task.FromResult(new Note("ok"))));
        var store = host.Services.GetRequiredService<IUploadStore>();
        var report = new StringBuilder();

        report.AppendLine("Finished uploads | removal with none expired (ms) | removal of all (ms, ms each)");

        foreach (int count in new[] { 1_000, 10_000 })
        {
            await SeedAsync(store, count);

            double none = await TimeAsync(() => store.RemoveOutcomesAsync(DateTime.UtcNow.AddHours(-1), TestContext.CancellationToken).AsTask());
            double all = await TimeAsync(() => store.RemoveOutcomesAsync(DateTime.UtcNow.AddMinutes(1), TestContext.CancellationToken).AsTask());

            report.AppendLine(Invariant($"{count,6:N0} | {none:F1} | {all:F0} ({all / count:F3})"));
        }

        await WriteReportAsync(report);
    }

    private async Task WriteReportAsync(StringBuilder report)
    {
        TestContext.WriteLine(report.ToString());

        if (Environment.GetEnvironmentVariable("HTTPAPI_BENCHMARK_OUTPUT") is { Length: > 0 } output)
            await File.AppendAllTextAsync(output, $"{TestContext.TestName}\n{report}\n", TestContext.CancellationToken);
    }

    /// <summary>
    /// Times 256 creations, 32 at a time, as the toolkit's client makes them, and aborts them afterwards.
    /// </summary>
    /// <returns>The creations a second.</returns>
    private async Task<double> TimeConcurrentCreationsAsync(UploadTestHost host)
    {
        var sessions = new System.Collections.Concurrent.ConcurrentBag<DataContracts.UploadSession>();
        long start = Stopwatch.GetTimestamp();

        await Parallel.ForAsync(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (_, cancellationToken) => {
            for (int i = 0; i < 8; i++)
            {
                var body = new { fileName = "photo.jpg", length = FileLength, idempotencyKey = Guid.NewGuid().ToString("N") };
                using var response = await host.SendCreateAsync(body);
                response.StatusCode.ShouldBe(HttpStatusCode.Created);
                sessions.Add((await response.Content.ReadFromJsonAsync<DataContracts.UploadSession>(UploadTestHost.JsonOptions, cancellationToken))!);
            }
        });

        double perSecond = 256 / Stopwatch.GetElapsedTime(start).TotalSeconds;

        foreach (var session in sessions)
            (await host.AbortAsync(session)).Dispose();

        return perSecond;
    }

    /// <summary>
    /// Makes direct uploads of a file, 32 at a time, for 10 seconds, as the users take turns, and times them.
    /// </summary>
    private async Task<(double PerSecond, List<double> Times)> TimeDirectThroughputAsync(UploadTestHost host, byte[] file, int users)
    {
        var times = new System.Collections.Concurrent.ConcurrentBag<double>();
        long start = Stopwatch.GetTimestamp();

        await Parallel.ForAsync(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (worker, cancellationToken) => {
            while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(10))
            {
                long uploadStart = Stopwatch.GetTimestamp();
                var request = host.CreateDirectRequest(new ByteArrayContent(file), key: Guid.NewGuid().ToString("N"), userId: 1 + (worker % users));

                using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.IsSuccessStatusCode.ShouldBeTrue();
                (await UploadTestHost.ReadLinesAsync(response))[^1].ShouldBe("""{"end":true}""");

                times.Add(Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds);
            }
        });

        return (times.Count / Stopwatch.GetElapsedTime(start).TotalSeconds, [.. times]);
    }

    /// <summary>
    /// Stores finished uploads of the user, each with an idempotency key, as uploads that the user made earlier leave them.
    /// </summary>
    private async Task<List<string>> SeedAsync(IUploadStore store, int count)
    {
        string outcome = """{"hasItem":true,"item":{"text":"ok"}}""";
        string[] ids = [.. Enumerable.Range(0, count).Select(_ => CreateId())];

        await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, cancellationToken) => {
            var record = new UploadRecord(ids[i], UserId, FileLength, 5 << 20, DateTime.UtcNow, "{}");

            (await store.TryCreateAsync(record, cancellationToken)).ShouldBe(UploadCreationResult.Created);
            (await store.TrySetOutcomeAsync(record, outcome, cancellationToken)).ShouldBeTrue();
        });

        return [.. ids];
    }

    /// <summary>
    /// Times reads of finished uploads, as a completion made again after its response was lost makes them, or returns <see langword="null"/> if there are
    /// none.
    /// </summary>
    private async Task<List<double>?> TimeReadsAsync(IUploadStore store, List<string> finishedIds)
    {
        if (finishedIds.Count is 0)
            return null;

        List<double> times = [];

        for (int i = 0; i < Creations; i++)
        {
            string id = finishedIds[Random.Shared.Next(finishedIds.Count)];
            long start = Stopwatch.GetTimestamp();

            (await store.GetAsync(UserId, id, TestContext.CancellationToken)).ShouldNotBeNull().Outcome.ShouldNotBeNull();
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return times;
    }

    /// <summary>
    /// Times creations as the toolkit's client makes them, with an idempotency key, aborting each after it is timed so that the user stays under the limit
    /// of open uploads.
    /// </summary>
    private async Task<List<double>> TimeCreationsAsync(UploadTestHost host)
    {
        List<double> times = [];

        for (int i = 0; i < Creations; i++)
        {
            var body = new { fileName = "photo.jpg", length = FileLength, idempotencyKey = Guid.NewGuid().ToString("N") };
            long start = Stopwatch.GetTimestamp();

            using var response = await host.SendCreateAsync(body);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.CancellationToken));

            var session = (await response.Content.ReadFromJsonAsync<DataContracts.UploadSession>(UploadTestHost.JsonOptions, TestContext.CancellationToken))!;
            (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        return times;
    }

    /// <summary>
    /// Times whole uploads of a small file with the three requests: the creation, the chunk and the completion.
    /// </summary>
    private async Task<List<double>> TimeChunkedUploadsAsync(UploadTestHost host)
    {
        byte[] file = UploadTestHost.CreateFile(FileLength);
        List<double> times = [];

        for (int i = 0; i < Uploads; i++)
        {
            long start = Stopwatch.GetTimestamp();
            var (_, lines) = await host.UploadAsync(file);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);

            lines[^1].ShouldBe("""{"end":true}""");
        }

        return times;
    }

    /// <summary>
    /// Times direct uploads of a small file, in one request, or returns <see langword="null"/> if the service has no direct uploads.
    /// </summary>
    private async Task<List<double>?> TimeDirectUploadsAsync(UploadTestHost host)
    {
        byte[] file = UploadTestHost.CreateFile(FileLength);
        List<double> times = [];

        for (int i = 0; i < Uploads; i++)
        {
            var request = host.CreateDirectRequest(new ByteArrayContent(file), key: Guid.NewGuid().ToString("N"));

            long start = Stopwatch.GetTimestamp();
            using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.CancellationToken);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                return null;

            response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
            var lines = await UploadTestHost.ReadLinesAsync(response);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);

            lines[^1].ShouldBe("""{"end":true}""");
        }

        return times;
    }

    private async Task<(double Milliseconds, int Count)> TimeListingAsync(IUploadStore store)
    {
        int count = 0;
        long start = Stopwatch.GetTimestamp();

        await foreach (var upload in store.ListAsync(cancellationToken: TestContext.CancellationToken))
            count++;

        return (Stopwatch.GetElapsedTime(start).TotalMilliseconds, count);
    }

    private static async Task<double> TimeAsync(Func<Task> action)
    {
        long start = Stopwatch.GetTimestamp();
        await action();

        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static double Median(List<double> values) => Percentile(values, 0.5);

    private static double Percentile(List<double> values, double fraction)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(fraction * sorted.Count))];
    }

    private static string CreateId() => Guid.NewGuid().ToString("D");

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// Runs a test only when the <c>HTTPAPI_BENCHMARKS</c> environment variable is <c>1</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class BenchmarkAttribute : ConditionBaseAttribute
    {
        public BenchmarkAttribute() : base(ConditionMode.Include)
        {
            IgnoreMessage = "Benchmarks run only with HTTPAPI_BENCHMARKS=1.";
        }

        public override bool IsConditionMet => Environment.GetEnvironmentVariable("HTTPAPI_BENCHMARKS") is "1";

        public override string GroupName => "Benchmark";
    }
}
