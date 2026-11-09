using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The toolkit's meter, <see cref="MeterName"/>, and its instruments (see the diagnostics guide). There is one for each <see cref="IMeterFactory"/>, and so
/// for each service provider, which the components that report to it share. A counter adds nothing while no listener is attached, and the observable
/// instruments read fields only when they are collected.
/// </summary>
internal sealed class HttpApiMetrics
{
    /// <summary>
    /// The name of the meter.
    /// </summary>
    public const string MeterName = "Singulink.Net.Http.Api.Service";

    public const string StoreKeyTag = "httpapi.short_lived_store.key";
    public const string UploadKindTag = "httpapi.upload.kind";
    public const string UploadEndTag = "httpapi.upload.end";
    public const string UploadRejectionTag = "httpapi.upload.rejection";
    public const string UploadCancellationTag = "httpapi.upload.cancellation";
    public const string UploadStoreOperationTag = "httpapi.upload.store.operation";
    public const string CleanupResultTag = "httpapi.upload.cleanup.result";
    public const string PolicyTag = "httpapi.rate_limiting.policy";
    public const string RateLimitReasonTag = "httpapi.rate_limiting.reason";
    public const string TokenTypeTag = "httpapi.session.token_type";

    // The store tag of the store registered without a key.
    public const string DefaultStoreKey = "default";

    private static readonly ConditionalWeakTable<IMeterFactory, HttpApiMetrics> All = [];

    private readonly Lock _lock = new();
    private readonly List<(string Key, WeakReference<MemoryShortLivedStore> Store)> _stores = [];
    private readonly List<RateLimitPolicyCounters> _policies = [];
    private readonly List<ISessionConnectionTracker> _trackers = [];

    private readonly Counter<long> _uploadsCreated;
    private readonly Counter<long> _uploadsEnded;
    private readonly Counter<long> _uploadsRejected;
    private readonly Counter<long> _uploadCancellations;
    private readonly Counter<long> _uploadStoreFailures;
    private readonly Histogram<double> _cleanupDuration;
    private readonly Counter<long> _rateLimitRejections;
    private readonly Counter<long> _hubConnectionsEnded;

    private IUploadsMetricsSource? _uploads;

    private HttpApiMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName, typeof(HttpApiMetrics).Assembly.GetName().Version?.ToString());
        Meter = meter;

        meter.CreateObservableUpDownCounter(
            "httpapi.short_lived_store.entry.count", () => ObserveStores(s => s.EntryCount), "{entry}", "Entries in a memory short-lived store.");
        meter.CreateObservableUpDownCounter(
            "httpapi.short_lived_store.usage", () => ObserveStores(s => s.UsedBytes), "By", "Estimated size of a memory short-lived store's entries.");
        meter.CreateObservableUpDownCounter(
            "httpapi.short_lived_store.limit", () => ObserveStores(s => s.MaxBytes), "By", "Size limit of a memory short-lived store.");
        meter.CreateObservableCounter(
            "httpapi.short_lived_store.refused_writes",
            () => ObserveStores(s => s.RefusedWrites),
            "{write}",
            "Writes that a memory short-lived store refused because it was full.");

        meter.CreateObservableUpDownCounter(
            "httpapi.upload.running_handlers", () => ObserveUploads(u => u.RunningHandlers), "{upload}", "Upload handlers running in this process.");
        meter.CreateObservableUpDownCounter(
            "httpapi.upload.pending_claim_releases",
            () => ObserveUploads(u => u.PendingClaimReleases),
            "{claim}",
            "Claims of upload runs that this server could not release yet and releases in the background.");
        meter.CreateObservableGauge(
            "httpapi.upload.open.count", () => ObserveUploads(u => u.OpenUploads), "{upload}", "Open uploads, as of the last cleanup.");
        meter.CreateObservableGauge(
            "httpapi.upload.open.size", () => ObserveUploads(u => u.OpenUploadsSize), "By", "Total length of the open uploads, as of the last cleanup.");
        meter.CreateObservableGauge(
            "httpapi.upload.store.available",
            () => ObserveUploads(u => u.StoreAvailableBytes),
            "By",
            "Space available to the upload store, where the store can tell.");

        _uploadsCreated = meter.CreateCounter<long>("httpapi.upload.created", "{upload}", "Uploads created.");
        _uploadsEnded = meter.CreateCounter<long>("httpapi.upload.ended", "{upload}", "Uploads that ended, by how they ended.");
        _uploadsRejected = meter.CreateCounter<long>("httpapi.upload.rejected", "{upload}", "Creations of uploads rejected by a limit or a full store.");
        _uploadCancellations = meter.CreateCounter<long>("httpapi.upload.cancellations", "{request}", "Cancellations of uploads, by their answer.");
        _uploadStoreFailures = meter.CreateCounter<long>(
            "httpapi.upload.store.failures", "{operation}", "Operations of the upload store that failed and were reported as unavailable.");
        _cleanupDuration = meter.CreateHistogram<double>("httpapi.upload.cleanup.duration", "s", "Duration of the cleanups of uploads.");

        meter.CreateObservableUpDownCounter(
            "httpapi.rate_limiting.queued_requests",
            ObservePolicies,
            "{request}",
            "Requests waiting for the next window of a per-client limit on this server.");
        _rateLimitRejections = meter.CreateCounter<long>(
            "httpapi.rate_limiting.rejections", "{request}", "Requests that a per-client limit refused, by policy and reason.");

        meter.CreateObservableUpDownCounter(
            "httpapi.session.hub_connections", ObserveTrackers, "{connection}", "Hub connections with a session that this server tracks.");
        _hubConnectionsEnded = meter.CreateCounter<long>(
            "httpapi.session.hub_connections_ended", "{connection}", "Hub connections ended by a recheck because their session ended.");
    }

    /// <summary>
    /// Gets the toolkit's meter, for the instruments of the toolkit's other packages.
    /// </summary>
    public Meter Meter { get; }

    /// <summary>
    /// Gets the metrics of a meter factory.
    /// </summary>
    public static HttpApiMetrics Get(IMeterFactory meterFactory) => All.GetValue(meterFactory, f => new HttpApiMetrics(f));

    /// <summary>
    /// Gets the metrics of a service provider's meter factory, or <see langword="null"/> if it has none.
    /// </summary>
    public static HttpApiMetrics? Get(IServiceProvider services) => services.GetService<IMeterFactory>() is { } factory ? Get(factory) : null;

    public void AddStore(string? key, MemoryShortLivedStore store)
    {
        lock (_lock)
            _stores.Add((key ?? DefaultStoreKey, new WeakReference<MemoryShortLivedStore>(store)));
    }

    public void SetUploads(IUploadsMetricsSource uploads) => Volatile.Write(ref _uploads, uploads);

    public void AddPolicy(RateLimitPolicyCounters policy)
    {
        lock (_lock)
            _policies.Add(policy);
    }

    public void AddTracker(ISessionConnectionTracker tracker)
    {
        lock (_lock)
            _trackers.Add(tracker);
    }

    public void UploadCreated(bool isDirect) => _uploadsCreated.Add(1, new KeyValuePair<string, object?>(UploadKindTag, isDirect ? "direct" : "chunked"));

    /// <summary>
    /// Counts an upload that ended: <c>succeeded</c>, <c>failed</c>, <c>cancelled</c>, <c>interrupted</c>, <c>expired</c> or <c>deleted</c>.
    /// </summary>
    public void UploadEnded(string end) => _uploadsEnded.Add(1, new KeyValuePair<string, object?>(UploadEndTag, end));

    /// <summary>
    /// Counts a creation that was rejected: <c>uploads_per_user</c>, <c>total_size_per_user</c>, <c>total_size</c>, <c>too_large</c> or <c>no_room</c>.
    /// </summary>
    public void UploadRejected(string reason) => _uploadsRejected.Add(1, new KeyValuePair<string, object?>(UploadRejectionTag, reason));

    /// <summary>
    /// Counts a cancellation by its answer: <c>deleted</c>, <c>signalled</c>, <c>finished</c> or <c>not_found</c>.
    /// </summary>
    public void UploadCancelled(string answer) => _uploadCancellations.Add(1, new KeyValuePair<string, object?>(UploadCancellationTag, answer));

    public void UploadStoreFailed(string operation) => _uploadStoreFailures.Add(1, new KeyValuePair<string, object?>(UploadStoreOperationTag, operation));

    public void CleanupEnded(TimeSpan duration, bool isCompleted)
    {
        _cleanupDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>(CleanupResultTag, isCompleted ? "completed" : "failed"));
    }

    /// <summary>
    /// Counts a request that a per-client limit refused: <c>limit</c> when the client is over it, and <c>store_unavailable</c> when the store couldn't
    /// count it. The global limiter has no policy, and its requests have no policy tag, as in ASP.NET Core's rate limiting metrics.
    /// </summary>
    public void RateLimitRejected(string? policy, string reason)
    {
        var reasonTag = new KeyValuePair<string, object?>(RateLimitReasonTag, reason);

        if (policy is null)
            _rateLimitRejections.Add(1, reasonTag);
        else
            _rateLimitRejections.Add(1, new KeyValuePair<string, object?>(PolicyTag, policy), reasonTag);
    }

    public void HubConnectionEnded(string tokenType) => _hubConnectionsEnded.Add(1, new KeyValuePair<string, object?>(TokenTypeTag, tokenType));

    private List<Measurement<long>> ObserveStores(Func<MemoryShortLivedStore, long> read)
    {
        List<Measurement<long>> measurements = [];

        lock (_lock)
        {
            // A store that is gone, such as one of a key that a test resolved and dropped, is no longer reported.
            _stores.RemoveAll(s => !s.Store.TryGetTarget(out _));

            foreach (var (key, reference) in _stores)
            {
                if (reference.TryGetTarget(out var store))
                    measurements.Add(new(read(store), new KeyValuePair<string, object?>(StoreKeyTag, key)));
            }
        }

        return measurements;
    }

    private IEnumerable<Measurement<long>> ObserveUploads(Func<IUploadsMetricsSource, long?> read)
    {
        if (Volatile.Read(ref _uploads) is { } uploads && read(uploads) is { } value)
            return [new Measurement<long>(value)];

        return [];
    }

    private List<Measurement<long>> ObservePolicies()
    {
        lock (_lock)
        {
            return [.. _policies.Select(p => p.Policy is null
                ? new Measurement<long>(p.QueuedRequests)
                : new Measurement<long>(p.QueuedRequests, new KeyValuePair<string, object?>(PolicyTag, p.Policy)))];
        }
    }

    private List<Measurement<long>> ObserveTrackers()
    {
        lock (_lock)
        {
            return [.. _trackers.Select(t => new Measurement<long>(
                t.GetStatus().TrackedConnections, new KeyValuePair<string, object?>(TokenTypeTag, t.TokenTypeName)))];
        }
    }
}
