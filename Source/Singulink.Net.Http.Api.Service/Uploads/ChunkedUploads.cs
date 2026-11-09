using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Handles the requests of chunked uploads, runs their handlers and removes the uploads that are no longer needed.
/// </summary>
internal sealed partial class ChunkedUploads : IUploadsMetricsSource
{
    /// <summary>
    /// The name of the route parameter of the upload ID.
    /// </summary>
    public const string IdRouteParameter = "chunkedUploadId";

    /// <summary>
    /// How often expired uploads are removed and the counts of the open uploads are corrected.
    /// </summary>
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    private const int MaxNameLength = 255;
    private const int MaxIdempotencyKeyLength = 128;
    private const int MaxCreationLength = 64 * 1024;

    // The value of the cancellation of an upload in the short-lived store, which is only checked for.
    private const string CancellationValue = "1";

    // How long a creation waits while the user's upload with its ID is being removed, such as one that was cancelled a moment ago.
    private static readonly TimeSpan CreationWaitTimeout = TimeSpan.FromSeconds(5);

    // How often that creation tries again.
    private static readonly TimeSpan CreationPollInterval = TimeSpan.FromMilliseconds(100);

    // The retry time of that creation after its wait.
    private static readonly TimeSpan CreationRetryAfter = TimeSpan.FromSeconds(1);

    // The retry time of a request whose cancellation could not be written or read in the short-lived store.
    private static readonly TimeSpan CancellationRetryAfter = TimeSpan.FromSeconds(2);

    // How long the cancellation of an upload that doesn't exist yet is kept. A proxy forwards a request once it has its whole body, and drops it when the
    // client aborts it before then, so a request that the cancellation overtook arrives within seconds. A short lifetime bounds what a user can fill the
    // short-lived store with by cancelling uploads that don't exist.
    private static readonly TimeSpan EarlyCancellationLifetime = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan StorageFullRetryAfter = TimeSpan.FromMinutes(1);

    // How long the body of a rejected request is read before the rejection is answered anyway.
    private static readonly TimeSpan DiscardBodyTimeout = TimeSpan.FromMinutes(1);

    // Used when the app has no options service, as ASP.NET Core does for minimal API results.
    private static readonly JsonOptions DefaultJsonOptions = new();

    private readonly ChunkedUploadOptions _options;
    private readonly IUploadStore _store;
    private readonly IShortLivedStore _shortLivedStore;
    private readonly TimeProvider _timeProvider;

    // When this instance was created, after the upload store: a run of a process-local store that was claimed before then was left by a process that ended.
    private readonly DateTime _startedUtc;
    private readonly UploadUser _user;
    private readonly ILogger _logger;
    private readonly bool _isDevelopment;
    private readonly CancellationToken _stopping;
    private readonly string? _preconditionQueryName;
    private readonly ConcurrentDictionary<UserUploadId, UploadRun> _runs = new();

    // The claims that this server could not release yet, which it releases again in the background (see ReleaseClaimAsync).
    private readonly ConcurrentDictionary<UserUploadId, object> _releases = new();

    private readonly IUploadStore _rawStore;
    private readonly HttpApiMetrics? _metrics;

    // The tally of the last listing of the open uploads that completed, for the metrics.
    private OpenUploadCounters.Tally? _lastTally;

    // The largest upload that a mapped endpoint accepts, or -1 before an endpoint is mapped.
    private long _maxEndpointSize = -1;

    public ChunkedUploads(
        IOptions<ChunkedUploadOptions> options,
        [FromKeyedServices(ShortLivedStoreKeys.Uploads)] IShortLivedStore shortLivedStore,
        TimeProvider timeProvider,
        UploadUser user,
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _options.Freeze();
        _logger = loggerFactory.CreateLogger("Singulink.Net.Http.Api.Service.Uploads");
        user.EnsureSessionHandling(services);

        var store = services.GetService<IUploadStore>() ?? throw new InvalidOperationException(
            $"Chunked uploads require an {nameof(IUploadStore)}. Register the directory store with " +
            $"{nameof(ServiceCollectionExtensions.AddDirectoryUploadStore)}, or a store of the app's own as a singleton {nameof(IUploadStore)}.");

        CheckSharingMode(store.SharingMode, store.GetType().Name);
        CheckSharingMode(shortLivedStore.SharingMode, AppShortLivedStore.GetTypeName(shortLivedStore));

        if (store.SharingMode is StoreSharingMode.Shared && shortLivedStore.SharingMode is StoreSharingMode.ProcessLocal)
        {
            throw new InvalidOperationException(
                $"The upload store is shared with other processes, but the {nameof(IShortLivedStore)} of uploads " +
                $"({AppShortLivedStore.GetTypeName(shortLivedStore)}) keeps its values in this process, so the processes would not share the counts of open " +
                $"uploads and the cancellations of uploads. Register a shared {nameof(IShortLivedStore)}, for every feature or for the key " +
                $"{nameof(ShortLivedStoreKeys)}.{nameof(ShortLivedStoreKeys.Uploads)}, or use an upload store that only this process uses (see " +
                $"{nameof(DirectoryUploadStoreOptions)}.{nameof(DirectoryUploadStoreOptions.SharingMode)}).");
        }

        _metrics = HttpApiMetrics.Get(services);
        _rawStore = store;
        _store = new GuardedUploadStore(store, _logger, timeProvider, _metrics);
        _shortLivedStore = shortLivedStore;
        _timeProvider = timeProvider;
        _startedUtc = UtcNow;

        // The counts of the uploads of a store that only this process uses, in a short-lived store that other processes use too, are its machine's.
        bool isCountedPerMachine = store.SharingMode is StoreSharingMode.ProcessLocal && shortLivedStore.SharingMode is StoreSharingMode.Shared;
        IsCountedPerMachine = isCountedPerMachine;
        _counters = new OpenUploadCounters(shortLivedStore, isCountedPerMachine ? Environment.MachineName : null, _logger);
        _user = user;
        _isDevelopment = environment.IsDevelopment();
        _stopping = lifetime.ApplicationStopping;
        _preconditionQueryName = services.GetService<IOptions<SessionHandlingOptions>>()?.Value.UserIdPreconditionQueryName;

        MinChunkSize = Math.Max(_options.MinChunkSize, store.MinChunkSize);
        MaxChunkSize = Math.Min(_options.MaxChunkSize, store.MaxChunkSize);
        MaxChunkCount = Math.Min(_options.MaxChunkCount, store.MaxChunkCount);

        if (MinChunkSize > MaxChunkSize)
        {
            throw new InvalidOperationException(
                $"The chunk sizes of the upload store ({store.MinChunkSize} to {store.MaxChunkSize} bytes) and of {nameof(ChunkedUploadOptions)} " +
                $"({_options.MinChunkSize} to {_options.MaxChunkSize} bytes) have no size in common.");
        }

        if (MaxChunkCount < 1)
            throw new InvalidOperationException($"The upload store's largest number of chunks ({store.MaxChunkCount}) is not positive.");

        DefaultChunkSize = Math.Clamp(_options.DefaultChunkSize, MinChunkSize, MaxChunkSize);

        _metrics?.SetUploads(this);

        if (_options.MaxTotalSize is null)
        {
            _logger.LogWarning(
                "{Option} is not set, so the open uploads can take all the room of the upload store, each reserving its length when it is created.",
                $"{nameof(ChunkedUploadOptions)}.{nameof(ChunkedUploadOptions.MaxTotalSize)}");
        }
    }

    public int MinChunkSize { get; }

    /// <summary>
    /// Checks that a store reports whether it is process-local or shared, rather than <see cref="StoreSharingMode.Unspecified"/> or a value that isn't one
    /// of <see cref="StoreSharingMode"/>, such as a store of the app's own that leaves it unset.
    /// </summary>
    private static void CheckSharingMode(StoreSharingMode sharingMode, string storeName)
    {
        if (sharingMode is not (StoreSharingMode.ProcessLocal or StoreSharingMode.Shared))
        {
            throw new InvalidOperationException(
                $"{storeName} reports {sharingMode} as its {nameof(StoreSharingMode)}. A store reports {nameof(StoreSharingMode.ProcessLocal)} or " +
                $"{nameof(StoreSharingMode.Shared)}.");
        }
    }

    public int MaxChunkSize { get; }

    /// <summary>
    /// Gets the last cleanup, or <see langword="null"/> before the first one has ended.
    /// </summary>
    public UploadCleanupStatus? LastCleanup { get; private set; }

    /// <summary>
    /// Gets the options of chunked uploads.
    /// </summary>
    public ChunkedUploadOptions Options => _options;

    /// <summary>
    /// Gets the app's upload store, as it is, without the guard that reports its failures as unavailable.
    /// </summary>
    public IUploadStore Store => _rawStore;

    /// <summary>
    /// Gets the failures of the upload store since the app started, and when the last one was.
    /// </summary>
    public (long Count, DateTimeOffset? LastUtc) StoreFailures => ((GuardedUploadStore)_store).Failures;

    /// <summary>
    /// Gets a value indicating whether the counts of the open uploads are this machine's (see <see cref="OpenUploadCounters"/>).
    /// </summary>
    public bool IsCountedPerMachine { get; }

    long? IUploadsMetricsSource.RunningHandlers => RunningHandlers;

    long? IUploadsMetricsSource.PendingClaimReleases => PendingClaimReleases;

    long? IUploadsMetricsSource.OpenUploads => Volatile.Read(ref _lastTally)?.Uploads;

    long? IUploadsMetricsSource.OpenUploadsSize => Volatile.Read(ref _lastTally)?.TotalSize;

    long? IUploadsMetricsSource.StoreAvailableBytes => (_rawStore as DirectoryUploadStore)?.TryGetAvailableBytes();

    /// <summary>
    /// Gets the number of upload handlers running in this process.
    /// </summary>
    public int RunningHandlers => _runs.Count;

    /// <summary>
    /// Gets the number of claims that this server could not release yet, and releases in the background.
    /// </summary>
    public int PendingClaimReleases => _releases.Count;

    /// <summary>
    /// Gets the largest upload that the service accepts: the largest of the mapped endpoints, each its own
    /// <see cref="ChunkedUploadEndpointOptions.MaxSize"/> or <see cref="ChunkedUploadOptions.MaxSize"/>, or <see cref="ChunkedUploadOptions.MaxSize"/>
    /// before an endpoint is mapped.
    /// </summary>
    public long MaxUploadSize => Volatile.Read(ref _maxEndpointSize) is var size and >= 0 ? size : _options.MaxSize;

    /// <summary>
    /// Reads the total length of the open uploads as the limit of all users sees it (see <see cref="OpenUploadCounters.ReadTotalSizeAsync"/>).
    /// </summary>
    public ValueTask<long> ReadOpenUploadsSizeAsync(CancellationToken cancellationToken) => _counters.ReadTotalSizeAsync(cancellationToken);

    public int MaxChunkCount { get; }

    public int DefaultChunkSize { get; }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Gets how long an upload that has received no chunk is kept: the unstarted expiry, but no longer than the idle expiry.
    /// </summary>
    private TimeSpan UnstartedExpiry => _options.UnstartedExpiry < _options.IdleExpiry ? _options.UnstartedExpiry : _options.IdleExpiry;

    /// <summary>
    /// Records an endpoint that was mapped, for <see cref="MaxUploadSize"/>.
    /// </summary>
    public void AddEndpoint(UploadEndpoint endpoint)
    {
        // Endpoints are mapped as the app starts, one after the other.
        long size = endpoint.MaxSize ?? _options.MaxSize;

        if (size > _maxEndpointSize)
            Volatile.Write(ref _maxEndpointSize, size);
    }

    /// <summary>
    /// Creates an upload: <c>POST {path}/uploads</c>.
    /// </summary>
    public async Task CreateAsync(HttpContext context, UploadEndpoint endpoint)
    {
        string userId = await _user.GetUserIdAsync(context);
        var cancellationToken = context.RequestAborted;

        if (!context.Request.HasJsonContentType())
            throw UploadErrors.CreationNotJson();

        var creation = await ReadCreationAsync(context, cancellationToken) ?? throw UploadErrors.CreationUnreadable();
        CheckCreation(creation);

        var limits = await GetLimitsAsync(context, userId, cancellationToken);
        CheckLength(creation.Length, endpoint, limits);
        int chunkSize = ChooseChunkSize(creation.Length, creation.ChunkSize);

        var (record, existing, _) = await CreateOrGetAsync(context, endpoint, userId, creation, limits, chunkSize, 1, cancellationToken);

        // A repeated creation gets the upload that the first one created.
        if (existing is not null)
            await WriteSessionAsync(context, StatusCodes.Status200OK, existing.Record, GetExpiresUtc(existing), cancellationToken);
        else
            await WriteSessionAsync(context, StatusCodes.Status201Created, record, record.CreatedUtc + UnstartedExpiry, cancellationToken);
    }

    /// <summary>
    /// Checks the values of a creation that the service checks before anything else: the file's name, length and content type, the ID and the idempotency
    /// key.
    /// </summary>
    private static void CheckCreation(CreateUploadCommand creation)
    {
        if (string.IsNullOrWhiteSpace(creation.FileName) || creation.FileName.Length > MaxNameLength)
            throw UploadErrors.FileNameInvalid();

        if (creation.Length < 0)
            throw UploadErrors.LengthInvalid();

        if (creation.ContentType is { Length: > MaxNameLength })
            throw UploadErrors.ContentTypeInvalid();

        if (creation.Id == Guid.Empty)
            throw UploadErrors.IdInvalid();

        if (creation.IdempotencyKey is { Length: 0 or > MaxIdempotencyKeyLength })
            throw UploadErrors.IdempotencyKeyInvalid();
    }

    /// <summary>
    /// Creates an upload, or gets the user's upload with the creation's ID: checks the limits of the user's open uploads, runs the validator, and creates the
    /// upload within the limits.
    /// </summary>
    /// <param name="context">The context of the request that creates the upload.</param>
    /// <param name="endpoint">The upload endpoint.</param>
    /// <param name="userId">The ID of the request's user.</param>
    /// <param name="creation">The values of the creation, which <see cref="CheckCreation"/> has checked.</param>
    /// <param name="limits">The limits of the user's open uploads.</param>
    /// <param name="chunkSize">The chunk size of the upload.</param>
    /// <param name="ownSegmentCount">The number of segments of the toolkit's own endpoint at the end of the request's route pattern.</param>
    /// <param name="cancellationToken">A token that cancels the creation.</param>
    /// <returns>The record of the upload, the user's upload with the creation's ID that an earlier creation created, or <see langword="null"/> if the upload
    /// was created now, and the metadata of the creation.</returns>
    /// <exception cref="ConflictApiException">The user's upload with the creation's ID is for another endpoint, query string, file or command
    /// (<see cref="UploadErrorCodes.IdConflict"/>, or <see cref="UploadErrorCodes.KeyConflict"/> for the idempotency key of a 4.x client).</exception>
    /// <exception cref="ServiceUnavailableApiException">The user's upload with the creation's ID is being removed, and was not removed within the time the
    /// creation waits (<see cref="UploadErrorCodes.CreationPending"/>).</exception>
    private async Task<(UploadRecord Record, StoredUpload? Existing, UploadMetadata Metadata)> CreateOrGetAsync(
        HttpContext context,
        UploadEndpoint endpoint,
        string userId,
        CreateUploadCommand creation,
        OpenUploadCounters.Limits limits,
        int chunkSize,
        int ownSegmentCount,
        CancellationToken cancellationToken)
    {
        var serializerOptions = GetSerializerOptions(context);

        // The handler's result or items are written with the app's JSON options, so that a type that they lack fails the creation rather than the run.
        serializerOptions.GetTypeInfo(endpoint.ItemType);

        if (endpoint.CommandType is { } commandType)
            UploadCommands.Read(creation.Command, commandType, endpoint.IsCommandRequired, serializerOptions);

        var metadata = new UploadMetadata {
            Endpoint = endpoint.GetKey(context, ownSegmentCount),
            RouteValues = GetRouteValues(context),
            Query = GetQuery(context),
            FileName = creation.FileName,
            ContentType = creation.ContentType,
            Command = creation.Command,
        };

        // A creation made again with the ID of an upload gets the upload, whatever the limits are now, and without running the validator again. The ID of
        // a 4.x client's creation comes from its idempotency key, and one without a key gets a new ID, so it can't have an upload yet.
        string uploadId = creation.Id is { } id ? UploadIds.Format(id) : creation.IdempotencyKey is { } key ? UploadIds.FromKey(key) : UploadIds.Create();
        bool canExist = creation.Id is not null || creation.IdempotencyKey is not null;

        if (canExist && await GetExistingAsync(userId, uploadId, creation, metadata, cancellationToken) is { } existing)
            return (existing.Record, existing, metadata);

        if (await GetLimitErrorAsync(userId, creation.Length, limits, cancellationToken) is { } limitError)
            throw Rejected(limitError);

        if (endpoint.Validator is { } validator)
        {
            var request = new UploadRequest(creation.FileName, creation.Length, creation.ContentType);
            new UploadRequestBinding(request, creation.Command, serializerOptions).Set(context);
            HandlerInvocation.Start(context);

            await validator(context);
        }

        var record = new UploadRecord(uploadId, userId, creation.Length, chunkSize, UtcNow, metadata.Write());

        // The upload is counted before it is created, so that creations at the same time, also on other servers, stay within the limits. A request that is
        // cancelled stops before the counts change.
        cancellationToken.ThrowIfCancellationRequested();

        if (await _counters.TryAddAsync(record, limits) is var limit and not OpenUploadCounters.ExceededLimit.None)
            throw Rejected(GetNeverFitsError(record.Length, limits) ?? GetLimitError(limit, limits));

        bool isCreated = false;

        try
        {
            long waitStart = _timeProvider.GetTimestamp();

            while (true)
            {
                var result = await _store.TryCreateAsync(record, cancellationToken);

                if (result is UploadCreationResult.Created)
                {
                    isCreated = true;

                    // A direct upload's creation has two segments of the toolkit's own endpoint (uploads/direct), and another creation one (uploads).
                    _metrics?.UploadCreated(isDirect: ownSegmentCount is 2);

                    return (record, null, metadata);
                }

                if (result is UploadCreationResult.NoRoom)
                {
                    _metrics?.UploadRejected("no_room");
                    throw UploadErrors.StorageFull(StorageFullRetryAfter);
                }

                // Another creation with the ID created the upload in the meantime, or the user's upload with the ID is being removed, such as one that was
                // cancelled a moment ago, after which this creation creates it.
                if (await GetExistingAsync(userId, uploadId, creation, metadata, cancellationToken) is { } created)
                    return (created.Record, created, metadata);

                if (_timeProvider.GetElapsedTime(waitStart) >= CreationWaitTimeout)
                    throw UploadErrors.CreationPending(CreationRetryAfter);

                await Task.Delay(CreationPollInterval, _timeProvider, cancellationToken);
            }
        }
        finally
        {
            if (!isCreated)
                await _counters.SubtractAsync(record);
        }
    }

    /// <summary>
    /// Gets the user's upload with the ID of a creation, which an earlier creation with the ID created, unless it has expired, which this deletes.
    /// </summary>
    /// <returns>The upload, or <see langword="null"/> if the user has none with the ID.</returns>
    /// <exception cref="ConflictApiException">The upload is for another endpoint, query string, file or command.</exception>
    private async ValueTask<StoredUpload?> GetExistingAsync(
        string userId, string uploadId, CreateUploadCommand creation, UploadMetadata metadata, CancellationToken cancellationToken)
    {
        var upload = await _store.GetAsync(userId, uploadId, cancellationToken);

        if (upload is null)
            return null;

        if (IsExpired(upload, UtcNow))
        {
            if (await _store.DeleteAsync(upload.Record, CancellationToken.None))
            {
                await _counters.SubtractAsync(upload.Record);
                _metrics?.UploadEnded("expired");
            }

            return null;
        }

        if (UploadMetadata.Read(upload.Record.Metadata) is { } existing && IsSameUpload(existing, upload.Record.Length, metadata, creation.Length))
            return upload;

        throw creation.Id is null ? UploadErrors.KeyConflict() : UploadErrors.IdConflict();
    }

    /// <summary>
    /// Writes a chunk of an upload: <c>PUT {path}/uploads/{id}</c>.
    /// </summary>
    public async Task WriteChunkAsync(HttpContext context, UploadEndpoint endpoint)
    {
        var cancellationToken = context.RequestAborted;
        ChunkContentStream? content = null;
        ChunkWriteResult result;

        try
        {
            string userId = await _user.GetUserIdAsync(context);

            var (upload, _) = await FindAsync(context, endpoint, 2, userId, cancellationToken) ?? throw UploadErrors.NotFound();
            var record = upload.Record;

            if (upload.Outcome is null && _releases.ContainsKey(UserUploadId.Of(record)))
                throw UploadErrors.StoreUnavailable(GuardedUploadStore.RetryAfter);

            if (IsClosed(upload))
                throw UploadErrors.Closed();

            int index = GetChunkIndex(context.Request.Headers.ContentRange, record) ?? throw UploadErrors.ChunkRangeInvalid();
            int length = record.GetChunkLength(index);

            if (context.Request.ContentLength is { } contentLength && contentLength != length)
                throw UploadErrors.ChunkLengthInvalid();

            // The chunk's length is its request size limit, whatever the server's limit is. Some servers, such as the test server, have none.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maxRequestBodySize)
                maxRequestBodySize.MaxRequestBodySize = length;

            content = new ChunkContentStream(context.Request.Body, length);
            result = await _store.WriteChunkAsync(record, index, content, cancellationToken);
        }
        catch (Exception ex) when (content is { IsLengthMismatch: true })
        {
            throw UploadErrors.ChunkLengthInvalid(ex);
        }
        catch (Exception) when (content?.BodyException is { } bodyException)
        {
            // The body failed, such as with a BadHttpRequestException of a body that arrives too slowly, which is reported as such, whatever the store
            // made of it.
            ExceptionDispatchInfo.Capture(bodyException).Throw();
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A rejection before the body is stored reads the body first (see DiscardBodyAsync). A body that failed is handled above, and isn't read again.
            await DiscardBodyAsync(context);
            throw;
        }

        // A body that is not stored is read and left out: the run was claimed after the checks above, and the store then leaves the file as it is, or the store
        // has no room for it or the upload is gone.
        if (result is not ChunkWriteResult.Written)
            await DiscardBodyAsync(context);

        if (result is ChunkWriteResult.Closed)
            throw UploadErrors.Closed();

        if (result is ChunkWriteResult.NoRoom)
            throw UploadErrors.StorageFull(StorageFullRetryAfter);

        if (result is not ChunkWriteResult.Written)
            throw UploadErrors.NotFound();

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// Reads and discards the body of a request whose file is not stored, so that a client that is still sending the body gets the answer rather than a
    /// closed connection: servers close it once the rest of a body takes longer than a few seconds to arrive, such as Kestrel, and a client that is still
    /// sending reports that as a failure of the connection. The request is answered after a minute all the same, whether the rest has arrived or not. A body
    /// that is longer than the largest chunk, which the toolkit's client never sends, or that has no length, is left to the server. A body that has failed
    /// isn't read again: another read gives nothing, can hold the answer for the minute, and can fail with something else than the error to report.
    /// </summary>
    private async Task DiscardBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is not { } length || length > ChunkedUploadOptions.ChunkSizeCeiling)
            return;

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maxRequestBodySize)
            maxRequestBodySize.MaxRequestBodySize = length;

        using var timeout = new CancellationTokenSource(DiscardBodyTimeout, _timeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, timeout.Token);

        try
        {
            await context.Request.Body.CopyToAsync(Stream.Null, cancellation.Token);
        }
        catch (Exception ex) when (ex is IOException or BadHttpRequestException or OperationCanceledException)
        {
            // The client is gone, its body failed or it took too long, and the request is answered as it is.
        }
    }

    /// <summary>
    /// Gets the status of an upload: <c>GET {path}/uploads/{id}</c>.
    /// </summary>
    public async Task GetStatusAsync(HttpContext context, UploadEndpoint endpoint)
    {
        string userId = await _user.GetUserIdAsync(context);
        var cancellationToken = context.RequestAborted;

        var (upload, _) = await FindAsync(context, endpoint, 2, userId, cancellationToken) ?? throw UploadErrors.NotFound();

        if (upload.RunState is UploadRunState.Lapsed && !_runs.ContainsKey(UserUploadId.Of(upload.Record)))
            upload = await InterruptAsync(upload) ?? throw UploadErrors.NotFound();

        var record = upload.Record;
        var expiresUtc = GetExpiresUtc(upload);
        UploadStatus status;

        if (upload.Outcome is not null)
        {
            status = new UploadStatus(UploadState.Completed, record.Length, record.ChunkSize, [], expiresUtc);
        }
        else if (IsClosed(upload))
        {
            status = new UploadStatus(UploadState.Processing, record.Length, record.ChunkSize, [], expiresUtc);
        }
        else
        {
            var received = await _store.GetReceivedChunksAsync(record, cancellationToken);
            status = new UploadStatus(UploadState.Receiving, record.Length, record.ChunkSize, GetMissingChunks(received, record.ChunkCount), expiresUtc);
        }

        await context.Response.WriteAsJsonAsync(status, UploadJsonContext.Default.UploadStatus, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Cancels an upload: <c>DELETE {path}/uploads/{id}</c>. The cancellation is written to the short-lived store first, also for an upload that doesn't
    /// exist yet, and the upload is then looked at. An upload that does not exist is not an error (<c>204</c>), unless the ID can't be an upload's
    /// (<c>404</c>, as for the other requests of the upload), and a finished upload keeps its outcome (<c>409</c>, <see cref="UploadErrorCodes.Finished"/>).
    /// A running handler's cancellation token is cancelled, on this server now and on another server at its next check, and the run ends the upload
    /// (<c>202</c>). Any other upload is deleted (<c>204</c>): one whose handler isn't running, one whose claim lapsed, whose handler has stopped, and one
    /// whose claim this server releases in the background, whose handler isn't running either.
    /// </summary>
    /// <remarks>
    /// No handler starts once the cancellation is written: the run checks for it after it has claimed the run and before it starts the handler, so either
    /// this request finds the claim and leaves the upload to the run, or the run finds the cancellation. That includes an upload that a request creates
    /// later, such as a direct upload that a proxy held until it had the whole body, while the cancellation is kept: 2 minutes when the upload doesn't exist
    /// yet, and otherwise as long as a run can start on it.
    /// </remarks>
    public async Task AbortAsync(HttpContext context, UploadEndpoint endpoint)
    {
        string userId = await _user.GetUserIdAsync(context);

        if (!UploadIds.TryParse(context.Request.RouteValues[IdRouteParameter] as string, out string? uploadId))
            throw UploadErrors.NotFound();

        context.Response.StatusCode = StatusCodes.Status204NoContent;

        string endpointKey = endpoint.GetKey(context, 2);
        var routeValues = GetRouteValues(context);
        string cancelKey = GetCancelKey(userId, uploadId, endpointKey, routeValues);

        await AddCancellationAsync(cancelKey, userId, uploadId, EarlyCancellationLifetime);

        // The upload as it is once the cancellation is written: a run claimed after this read finds the cancellation. The read checks the upload's user and
        // endpoint, and deletes an upload that has expired.
        if (await FindAsync(uploadId, endpointKey, routeValues, userId, CancellationToken.None) is not var (upload, _))
        {
            _metrics?.UploadCancelled("not_found");
            return;
        }

        var record = upload.Record;

        if (upload.Outcome is not null)
        {
            _metrics?.UploadCancelled("finished");
            throw UploadErrors.Finished();
        }

        // The upload exists, so its cancellation outlasts any run that can start on it. This second write only makes the first one's lifetime longer: the
        // first must be there before the read, and only the read tells which lifetime it needs. The set replaces it without a moment in which it is missing.
        await AddCancellationAsync(cancelKey, userId, uploadId, Min(_options.ProcessingTimeout + LeaseDuration, MaxTimerDelay));

        if (_runs.TryGetValue(UserUploadId.Of(record), out var run))
        {
            try
            {
                run.Cancel(RunCancelReason.Aborted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A callback of the cancellation token of the handler of upload {UploadId} of user {UserId} failed.", record.Id, userId);
            }

            _metrics?.UploadCancelled("signalled");
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        // A run on another server finds the cancellation at its next check.
        if (upload.RunState is UploadRunState.Running && !_releases.ContainsKey(UserUploadId.Of(record)))
        {
            _metrics?.UploadCancelled("signalled");
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        if (await _store.DeleteAsync(record, CancellationToken.None))
        {
            await _counters.SubtractAsync(record);
            _metrics?.UploadEnded("deleted");
        }

        _metrics?.UploadCancelled("deleted");

        // No run can start on the upload now, unless a request creates it again (see MayBeCreatedAgain).
        if (!MayBeCreatedAgain(record))
            await RemoveCancellationAsync(record, cancelKey);
    }

    /// <summary>
    /// Writes the cancellation of an upload, to be kept for the lifetime from now on, also when it is written already. The run only checks that it exists.
    /// A failure of the short-lived store, such as one that can't be reached for a moment, is
    /// reported as <see cref="UploadErrorCodes.CancellationUnavailable"/>, which clients retry.
    /// </summary>
    private async ValueTask AddCancellationAsync(string cancelKey, string userId, string uploadId, TimeSpan lifetime)
    {
        try
        {
            await _shortLivedStore.SetAsync(cancelKey, CancellationValue, lifetime, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not ApiException)
        {
            _logger.LogWarning(ex, "The cancellation of upload {UploadId} of user {UserId} could not be written.", uploadId, userId);
            throw UploadErrors.CancellationUnavailable(CancellationRetryAfter, ex);
        }
    }

    /// <summary>
    /// Gets a value indicating whether a request can create an upload that was deleted again, and run its handler: a direct upload, which carries the whole
    /// file, of a client that chose the upload's ID, which a proxy can hold until it has the whole body, or which the client made again after a request that
    /// got no response. The cancellation of such an upload is kept until it expires, so that the handler of the upload that it creates never starts.
    /// </summary>
    /// <remarks>
    /// The service's largest chunk is compared, not the length up to which the client makes a direct upload, which is the client's own option: a direct
    /// upload longer than the largest chunk is refused, so the rule keeps every cancellation that it must, and those of some uploads that the client made
    /// in chunks.
    /// </remarks>
    private bool MayBeCreatedAgain(UploadRecord record) => record.Length <= MaxChunkSize;

    /// <summary>
    /// Gets the key of the cancellation of an upload in the short-lived store, which has the user, and a hash of the endpoint and route values, so that a
    /// cancellation only finds the user's upload of the path that it was made for, as the other requests do.
    /// </summary>
    private static string GetCancelKey(string userId, string uploadId, string endpointKey, Dictionary<string, string?> routeValues)
    {
        var scope = new StringBuilder(endpointKey);

        foreach (var (name, value) in routeValues.OrderBy(p => p.Key, StringComparer.Ordinal))
            scope.Append(CultureInfo.InvariantCulture, $"\n{name.Length}:{name}{value?.Length ?? -1}:{value}");

        return $"{ShortLivedKeys.UploadCancel}{uploadId}:{ShortLivedId.Hash(scope.ToString())}:{userId}";
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// Reads the creation of an upload from the request's body, which can be up to 64 KiB long.
    /// </summary>
    private static async Task<CreateUploadCommand?> ReadCreationAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > MaxCreationLength)
            throw UploadErrors.CreationTooLarge(MaxCreationLength);

        // Servers that have a request size limit stop reading the body at it. Some, such as the test server, have none, and a server whose limit can't be
        // changed for the request stops at its own.
        bool isLimitSet = false;

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maxRequestBodySize)
        {
            maxRequestBodySize.MaxRequestBodySize = MaxCreationLength;
            isLimitSet = true;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxCreationLength + 1);

        try
        {
            int length = await context.Request.Body.ReadAtLeastAsync(
                buffer.AsMemory(0, MaxCreationLength + 1), MaxCreationLength + 1, throwOnEndOfStream: false, cancellationToken);

            if (length > MaxCreationLength)
                throw UploadErrors.CreationTooLarge(MaxCreationLength);

            return JsonSerializer.Deserialize(buffer.AsSpan(0, length), UploadJsonContext.Default.CreateUploadCommand);
        }
        catch (JsonException ex)
        {
            throw UploadErrors.CreationUnreadable(ex);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode is StatusCodes.Status413PayloadTooLarge)
        {
            // The body is longer than the request size limit: the largest creation, or the server's own.
            throw UploadErrors.CreationTooLarge(isLimitSet ? MaxCreationLength : null, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Dictionary<string, string?> GetRouteValues(HttpContext context)
    {
        var routeValues = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (name, value) in context.Request.RouteValues)
        {
            if (name != IdRouteParameter)
                routeValues[name] = Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return routeValues;
    }

    private static bool RouteValuesEqual(Dictionary<string, string?> stored, Dictionary<string, string?> current)
    {
        return stored.Count == current.Count && stored.All(p => current.TryGetValue(p.Key, out string? value) && value == p.Value);
    }

    /// <summary>
    /// Gets a value indicating whether the query string parameters of two requests are the same: the same names, compared without case as the binding of
    /// parameters does, each with the same values in the same order.
    /// </summary>
    private static bool QueriesEqual(Dictionary<string, string?[]> stored, Dictionary<string, string?[]> current)
    {
        var currentByName = new Dictionary<string, string?[]>(current, StringComparer.OrdinalIgnoreCase);
        return stored.Count == currentByName.Count && stored.All(p => currentByName.TryGetValue(p.Key, out var values) && values.SequenceEqual(p.Value));
    }

    /// <summary>
    /// Gets the query string parameters of a request without the user ID precondition, which can differ between the requests of an upload.
    /// </summary>
    private Dictionary<string, string?[]> GetQuery(HttpContext context)
    {
        var query = new Dictionary<string, string?[]>(StringComparer.Ordinal);

        foreach (var (name, values) in context.Request.Query)
        {
            if (!string.Equals(name, _preconditionQueryName, StringComparison.OrdinalIgnoreCase))
                query[name] = [.. values];
        }

        return query;
    }

    /// <summary>
    /// Gets the index of the chunk that a <c>Content-Range</c> header covers, or <see langword="null"/> if it does not cover exactly one chunk of the
    /// upload.
    /// </summary>
    private static int? GetChunkIndex(string? contentRange, UploadRecord record)
    {
        if (!ContentRangeHeaderValue.TryParse(contentRange, out var range) || range.Unit != "bytes" || range.Length != record.Length ||
            range is not { From: { } from, To: { } to })
        {
            return null;
        }

        if (from % record.ChunkSize is not 0 || from / record.ChunkSize >= record.ChunkCount)
            return null;

        int index = (int)(from / record.ChunkSize);
        return to == from + record.GetChunkLength(index) - 1 ? index : null;
    }

    /// <summary>
    /// Gets the ranges of the indexes of the chunks that have not been received.
    /// </summary>
    private static List<ChunkRange> GetMissingChunks(IReadOnlyCollection<int> received, int chunkCount)
    {
        var isReceived = new bool[chunkCount];

        foreach (int index in received)
        {
            if (index >= 0 && index < chunkCount)
                isReceived[index] = true;
        }

        List<ChunkRange> missing = [];

        for (int index = 0; index < chunkCount; index++)
        {
            if (isReceived[index])
                continue;

            int first = index;

            while (index + 1 < chunkCount && !isReceived[index + 1])
                index++;

            missing.Add(new ChunkRange(first, index));
        }

        return missing;
    }

    private static JsonSerializerOptions GetSerializerOptions(HttpContext context)
    {
        return (context.RequestServices.GetService<IOptions<JsonOptions>>()?.Value ?? DefaultJsonOptions).SerializerOptions;
    }

    /// <summary>
    /// Chooses the chunk size of an upload: the size the client asks for, or the default, within the chunk size bounds, raised if the upload would
    /// otherwise have too many chunks.
    /// </summary>
    private int ChooseChunkSize(long length, int? requestedSize)
    {
        int chunkSize;

        if (requestedSize is { } size)
        {
            if (size <= 0)
                throw UploadErrors.ChunkSizeInvalid();

            chunkSize = Math.Clamp(size, MinChunkSize, MaxChunkSize);
        }
        else
        {
            chunkSize = DefaultChunkSize;
        }

        if (DivideRoundingUp(length, chunkSize) > MaxChunkCount)
        {
            long neededSize = DivideRoundingUp(length, MaxChunkCount);

            if (neededSize > MaxChunkSize)
                throw UploadErrors.TooLarge((long)MaxChunkSize * MaxChunkCount);

            chunkSize = (int)neededSize;
        }

        return chunkSize;
    }

    private static long DivideRoundingUp(long value, long divisor) => (value / divisor) + (value % divisor > 0 ? 1 : 0);

    /// <summary>
    /// Gets a value indicating whether no chunk of an upload has arrived: its last write is its creation.
    /// </summary>
    private static bool IsUnstarted(StoredUpload upload) => upload.LastWriteUtc <= upload.Record.CreatedUtc;

    private static bool IsSameUpload(UploadMetadata metadata, long length, UploadMetadata otherMetadata, long otherLength)
    {
        return metadata.Endpoint == otherMetadata.Endpoint && RouteValuesEqual(metadata.RouteValues, otherMetadata.RouteValues) &&
            QueriesEqual(metadata.Query, otherMetadata.Query) && metadata.FileName == otherMetadata.FileName && length == otherLength &&
            metadata.ContentType == otherMetadata.ContentType && CommandsEqual(metadata.Command, otherMetadata.Command);
    }

    private static bool CommandsEqual(JsonElement? stored, JsonElement? current)
    {
        bool hasStored = stored is { ValueKind: not JsonValueKind.Null };
        bool hasCurrent = current is { ValueKind: not JsonValueKind.Null };

        return hasStored == hasCurrent && (!hasStored || JsonElement.DeepEquals(stored!.Value, current!.Value));
    }

    private async ValueTask WriteSessionAsync(HttpContext context, int statusCode, UploadRecord record, DateTime expiresUtc, CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.StatusCode = statusCode;
        response.Headers.Location = GetUploadPath(context, record.Id, segmentsAfterUploads: 0);

        var session = new UploadSession(record.Id, record.ChunkSize, expiresUtc);
        await response.WriteAsJsonAsync(session, UploadJsonContext.Default.UploadSession, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Gets the path of an upload, <c>{path}/uploads/{id}</c>, from the path of the request that created it: <c>{path}/uploads</c>, or
    /// <c>{path}/uploads/direct</c> for a direct upload. The path is escaped, since the header can't have characters that aren't ASCII, and a decoded
    /// character such as <c>?</c> or <c>%</c> would change its meaning.
    /// </summary>
    /// <param name="context">The context of the request that created the upload.</param>
    /// <param name="uploadId">The ID of the upload.</param>
    /// <param name="segmentsAfterUploads">The number of segments of the request's path after <c>{path}/uploads</c>.</param>
    private static string GetUploadPath(HttpContext context, string uploadId, int segmentsAfterUploads)
    {
        string path = EscapePath(context.Request.PathBase.Add(context.Request.Path).Value ?? string.Empty).TrimEnd('/');

        for (int i = 0; i < segmentsAfterUploads; i++)
            path = path[..path.LastIndexOf('/')];

        return $"{path}/{uploadId}";
    }

    /// <summary>
    /// Escapes a path that the server decoded (see <see cref="HttpRequest.Path"/>) for a URI, segment by segment with
    /// <see cref="Uri.EscapeDataString(string)"/>, keeping its slashes, and the escaped slashes (<c>%2F</c>) that the server leaves in a path.
    /// </summary>
    private static string EscapePath(string path)
    {
        var escaped = new StringBuilder(path.Length);
        int segmentStart = 0;

        for (int i = 0; i <= path.Length; i++)
        {
            bool isSlash = i < path.Length && path[i] is '/';
            bool isEscapedSlash = i + 2 < path.Length && path[i] is '%' && path[i + 1] is '2' && path[i + 2] is 'F' or 'f';

            if (i < path.Length && !isSlash && !isEscapedSlash)
                continue;

            escaped.Append(Uri.EscapeDataString(path[segmentStart..i]));

            if (isSlash)
                escaped.Append('/');
            else if (isEscapedSlash)
                escaped.Append("%2F");

            if (isEscapedSlash)
                i += 2;

            segmentStart = i + 1;
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Gets the time an upload expires: while it is receiving chunks, the idle expiry after its last chunk; while its handler runs, the processing timeout
    /// after the run started; and once it has finished, the outcome retention after its outcome.
    /// </summary>
    private DateTime GetExpiresUtc(StoredUpload upload)
    {
        if (upload.OutcomeUtc is { } outcomeUtc)
            return outcomeUtc + _options.OutcomeRetention;

        if (upload.RunStartedUtc is { } runStartedUtc)
            return runStartedUtc + _options.ProcessingTimeout;

        return GetReceivingExpiresUtc(upload);
    }

    /// <summary>
    /// Gets the time an upload that is receiving chunks expires: the unstarted expiry after its creation if no chunk has arrived, and the idle expiry after
    /// its last chunk otherwise.
    /// </summary>
    private DateTime GetReceivingExpiresUtc(StoredUpload upload)
    {
        return IsUnstarted(upload) ? upload.Record.CreatedUtc + UnstartedExpiry : upload.LastWriteUtc + _options.IdleExpiry;
    }

    /// <summary>
    /// Gets a value indicating whether an upload takes no more chunks: it has an outcome, or its run is claimed or is starting on this server.
    /// </summary>
    private bool IsClosed(StoredUpload upload)
    {
        return upload.Outcome is not null || upload.RunState is not UploadRunState.None || _runs.ContainsKey(UserUploadId.Of(upload.Record));
    }

    private bool IsExpired(StoredUpload upload, DateTime nowUtc)
    {
        if (upload.OutcomeUtc is { } outcomeUtc)
            return outcomeUtc + _options.OutcomeRetention <= nowUtc;

        return upload.RunState is UploadRunState.None && GetReceivingExpiresUtc(upload) <= nowUtc && !_runs.ContainsKey(UserUploadId.Of(upload.Record));
    }

    /// <summary>
    /// Finds the upload that a request is for, which must belong to the request's user, endpoint and route values, and must not have expired.
    /// </summary>
    private async ValueTask<(StoredUpload Upload, UploadMetadata Metadata)?> FindAsync(
        HttpContext context, UploadEndpoint endpoint, int ownSegmentCount, string userId, CancellationToken cancellationToken)
    {
        if (!UploadIds.TryParse(context.Request.RouteValues[IdRouteParameter] as string, out string? uploadId))
            return null;

        return await FindAsync(uploadId, endpoint.GetKey(context, ownSegmentCount), GetRouteValues(context), userId, cancellationToken);
    }

    /// <summary>
    /// Finds an upload by its ID, which must belong to the user, the endpoint and the route values, and must not have expired.
    /// </summary>
    private async ValueTask<(StoredUpload Upload, UploadMetadata Metadata)?> FindAsync(
        string uploadId, string endpointKey, Dictionary<string, string?> routeValues, string userId, CancellationToken cancellationToken)
    {
        var upload = await _store.GetAsync(userId, uploadId, cancellationToken);

        if (upload is null || upload.Record.Id != uploadId || upload.Record.UserId != userId)
            return null;

        if (UploadMetadata.Read(upload.Record.Metadata) is not { } metadata)
        {
            _logger.LogError("The metadata of an upload cannot be read.");
            return null;
        }

        if (metadata.Endpoint != endpointKey || !RouteValuesEqual(metadata.RouteValues, routeValues))
            return null;

        if (IsExpired(upload, UtcNow))
        {
            await DeleteExpiredAsync(upload.Record);
            return null;
        }

        return (upload, metadata);
    }

    /// <summary>
    /// Ends a run whose claim lapsed, because the server that ran the handler stopped, with the outcome of an interrupted upload. The handler does not run
    /// again.
    /// </summary>
    /// <returns>The upload as it is now, or <see langword="null"/> if it no longer exists.</returns>
    private async ValueTask<StoredUpload?> InterruptAsync(StoredUpload upload)
    {
        var record = upload.Record;

        if (await _store.TrySetOutcomeAsync(record, UploadOutcome.Failure(UploadErrors.Interrupted()).Write(), CancellationToken.None))
        {
            await _counters.SubtractAsync(record);
            _metrics?.UploadEnded("interrupted");

            _logger.LogWarning(
                "The run of the handler of upload {UploadId} of user {UserId} was claimed {StartedUtc:O} and stopped without an outcome, so the upload is " +
                "ended as interrupted.",
                record.Id,
                record.UserId,
                upload.RunStartedUtc);
        }

        return await _store.GetAsync(record.UserId, record.Id, CancellationToken.None);
    }

    private async ValueTask DeleteExpiredAsync(UploadRecord record)
    {
        try
        {
            if (await _store.DeleteAsync(record, CancellationToken.None))
            {
                await _counters.SubtractAsync(record);
                _metrics?.UploadEnded("expired");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload {UploadId} of user {UserId} could not be deleted.", record.Id, record.UserId);
        }
    }

    /// <summary>
    /// The user and the ID of an upload, by which the service keeps what it knows of the upload: the IDs of uploads are unique for their user only.
    /// </summary>
    private readonly record struct UserUploadId(string UserId, string UploadId)
    {
        public static UserUploadId Of(UploadRecord record) => new(record.UserId, record.Id);
    }
}
