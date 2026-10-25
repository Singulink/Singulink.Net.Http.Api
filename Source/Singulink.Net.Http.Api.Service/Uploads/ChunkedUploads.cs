using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Handles the requests of chunked uploads, runs their handlers and removes the uploads that are no longer needed.
/// </summary>
internal sealed partial class ChunkedUploads
{
    /// <summary>
    /// The name of the route parameter of the upload ID.
    /// </summary>
    public const string IdRouteParameter = "chunkedUploadId";

    /// <summary>
    /// How often expired uploads are removed and the total size of the open ones is computed.
    /// </summary>
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    private const int MaxNameLength = 255;
    private const int MaxIdempotencyKeyLength = 128;
    private const int MaxCreationLength = 64 * 1024;

    // A creation whose idempotency key an earlier one reserved waits this long for its upload, checking at the interval, and is answered with a retry time
    // after it. A key reserved this long ago for an upload that does not exist belongs to a creation that stopped without releasing it.
    private static readonly TimeSpan KeyWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan KeyRetryAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleKeyAge = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan StorageFullRetryAfter = TimeSpan.FromMinutes(1);

    // Used when the app has no options service, as ASP.NET Core does for minimal API results.
    private static readonly JsonOptions DefaultJsonOptions = new();

    private readonly ChunkedUploadOptions _options;
    private readonly IUploadStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly UploadUser _user;
    private readonly ILogger _logger;
    private readonly bool _isDevelopment;
    private readonly CancellationToken _stopping;
    private readonly string? _preconditionQueryName;
    private readonly ConcurrentDictionary<string, UploadRun> _runs = new(StringComparer.Ordinal);

    // The claims that this server could not release yet, by upload ID, which it releases again in the background (see ReleaseClaimAsync).
    private readonly ConcurrentDictionary<string, object> _releases = new(StringComparer.Ordinal);

    private readonly UploadTotal _total = new();
    private readonly KeyedLock _userLocks = new();

    public ChunkedUploads(
        IOptions<ChunkedUploadOptions> options,
        IUploadStore store,
        TimeProvider timeProvider,
        UploadUser user,
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _logger = loggerFactory.CreateLogger("Singulink.Net.Http.Api.Service.Uploads");
        _store = new GuardedUploadStore(store, _logger);
        _timeProvider = timeProvider;
        _user = user;
        _isDevelopment = environment.IsDevelopment();
        _stopping = lifetime.ApplicationStopping;
        _preconditionQueryName = services.GetService<IOptions<SessionHandlingOptions>>()?.Value.UserIdPreconditionQueryName;

        user.EnsureSessionHandling(services);

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

        if (_options.MaxTotalSize is null)
        {
            _logger.LogWarning(
                "{Option} is not set, so the open uploads can take all the room of the upload store, each reserving its length when it is created.",
                $"{nameof(ChunkedUploadOptions)}.{nameof(ChunkedUploadOptions.MaxTotalSize)}");
        }
    }

    public int MinChunkSize { get; }

    public int MaxChunkSize { get; }

    public int MaxChunkCount { get; }

    public int DefaultChunkSize { get; }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Gets how long an upload that has received no chunk is kept: the unstarted expiry, but no longer than the idle expiry.
    /// </summary>
    private TimeSpan UnstartedExpiry => _options.UnstartedExpiry < _options.IdleExpiry ? _options.UnstartedExpiry : _options.IdleExpiry;

    /// <summary>
    /// Creates an upload: <c>POST {path}/uploads</c>.
    /// </summary>
    public async Task CreateAsync(HttpContext context, UploadEndpoint endpoint)
    {
        string userId = await _user.GetUserIdAsync(context);
        var cancellationToken = context.RequestAborted;

        if (!context.Request.HasJsonContentType())
            throw UploadErrors.CreationNotJson();

        var command = await ReadCreationAsync(context, cancellationToken) ?? throw UploadErrors.CreationUnreadable();

        if (string.IsNullOrWhiteSpace(command.FileName) || command.FileName.Length > MaxNameLength)
            throw UploadErrors.FileNameInvalid();

        if (command.Length < 0)
            throw UploadErrors.LengthInvalid();

        if (command.ContentType is { Length: > MaxNameLength })
            throw UploadErrors.ContentTypeInvalid();

        if (command.IdempotencyKey is { Length: 0 or > MaxIdempotencyKeyLength })
            throw UploadErrors.IdempotencyKeyInvalid();

        long maxSize = endpoint.MaxSize ?? _options.MaxSize;

        // An upload that is longer than the open uploads of a user can be together can never be created. The error names the smaller of the two limits.
        if (_options.MaxTotalSizePerUser is { } maxSizePerUser && maxSizePerUser <= maxSize && command.Length > maxSizePerUser)
            throw UploadErrors.ExceedsSizeLimit(maxSizePerUser);

        if (command.Length > maxSize)
            throw UploadErrors.TooLarge(maxSize);

        int chunkSize = ChooseChunkSize(command.Length, command.ChunkSize);
        var serializerOptions = GetSerializerOptions(context);

        // The handler's result or items are written with the app's JSON options, so that a type that they lack fails the creation rather than the run.
        serializerOptions.GetTypeInfo(endpoint.ItemType);

        if (endpoint.CommandType is { } commandType)
            UploadCommands.Read(command.Command, commandType, endpoint.IsCommandRequired, serializerOptions);

        var metadata = new UploadMetadata {
            Endpoint = endpoint.GetKey(context, 1),
            RouteValues = GetRouteValues(context),
            Query = GetQuery(context),
            FileName = command.FileName,
            ContentType = command.ContentType,
            Command = command.Command,
        };

        string uploadId = ShortLivedId.Create();
        string? key = command.IdempotencyKey;
        UploadRecord record;

        try
        {
            if (key is not null && await ReserveKeyAsync(userId, key, uploadId, metadata, command.Length, cancellationToken) is { } existing)
            {
                // A repeated creation gets the upload that the first one created.
                await WriteSessionAsync(context, StatusCodes.Status200OK, existing.Record, GetExpiresUtc(existing), cancellationToken);
                return;
            }

            if (endpoint.Validator is { } validator)
            {
                var request = new UploadRequest(command.FileName, command.Length, command.ContentType);
                new UploadRequestBinding(request, command.Command, serializerOptions).Set(context);
                HandlerInvocation.Start(context);

                await validator(context);
            }

            // A user's creations count the user's open uploads and create the upload one at a time, and each reserves its length in the total before it
            // creates the upload, so that creations at the same time on this server stay within the limits.
            using (await _userLocks.EnterAsync(userId, cancellationToken))
            {
                var (openUploads, openLength) = await CountOpenUploadsAsync(userId, cancellationToken);

                if (openUploads >= _options.MaxUploadsPerUser)
                    throw UploadErrors.LimitReached(_options.MaxUploadsPerUser);

                if (_options.MaxTotalSizePerUser is { } maxTotalSizePerUser && openLength > maxTotalSizePerUser - command.Length)
                    throw UploadErrors.SizeLimitReached(maxTotalSizePerUser);

                var nowUtc = UtcNow;
                record = new UploadRecord(uploadId, userId, command.Length, chunkSize, nowUtc, metadata.Write(), key);

                if (!_total.TryReserve(record.Id, command.Length, _options.MaxTotalSize, nowUtc))
                    throw UploadErrors.StorageFull(StorageFullRetryAfter);

                bool isCreated = false;

                try
                {
                    isCreated = await _store.TryCreateAsync(record, cancellationToken);
                }
                finally
                {
                    _total.EndReservation(record.Id, new UploadLength(record.Length, record.CreatedUtc + UnstartedExpiry), isCreated);
                }

                if (!isCreated)
                    throw UploadErrors.StorageFull(StorageFullRetryAfter);
            }
        }
        catch when (key is not null)
        {
            // A creation made again with the key runs again. A release that fails leaves the creation's error as it is, and the reservation for later: the
            // directory store goes on removing it in the background, and a reservation that stays is taken over once it is stale.
            try
            {
                await _store.ReleaseKeyAsync(userId, key, uploadId, CancellationToken.None);
            }
            catch (ApiException ex) when (ex.ErrorCode is UploadErrorCodes.StoreUnavailable)
            {
            }

            throw;
        }

        await WriteSessionAsync(context, StatusCodes.Status201Created, record, record.CreatedUtc + UnstartedExpiry, cancellationToken);
    }

    /// <summary>
    /// Writes a chunk of an upload: <c>PUT {path}/uploads/{id}</c>.
    /// </summary>
    public async Task WriteChunkAsync(HttpContext context, UploadEndpoint endpoint)
    {
        string userId = await _user.GetUserIdAsync(context);
        var cancellationToken = context.RequestAborted;

        var (upload, _) = await FindAsync(context, endpoint, 2, userId, cancellationToken) ?? throw UploadErrors.NotFound();
        var record = upload.Record;

        if (upload.Outcome is null && _releases.ContainsKey(record.Id))
            throw UploadErrors.StoreUnavailable(GuardedUploadStore.RetryAfter);

        if (upload.Outcome is not null || upload.RunState is not UploadRunState.None || _runs.ContainsKey(record.Id))
            throw UploadErrors.Closed();

        int index = GetChunkIndex(context.Request.Headers.ContentRange, record) ?? throw UploadErrors.ChunkRangeInvalid();
        int length = record.GetChunkLength(index);

        if (context.Request.ContentLength is { } contentLength && contentLength != length)
            throw UploadErrors.ChunkLengthInvalid();

        // The chunk's length is its request size limit, whatever the server's limit is. Some servers, such as the test server, have none.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maxRequestBodySize)
            maxRequestBodySize.MaxRequestBodySize = length;

        var content = new ChunkContentStream(context.Request.Body, length);
        ChunkWriteResult result;

        try
        {
            result = await _store.WriteChunkAsync(record, index, content, cancellationToken);
        }
        catch (Exception ex) when (content.IsLengthMismatch)
        {
            throw UploadErrors.ChunkLengthInvalid(ex);
        }
        catch (Exception) when (content.BodyException is { } bodyException)
        {
            // The body failed, such as with a BadHttpRequestException of a body that arrives too slowly, which is reported as such, whatever the store
            // made of it.
            ExceptionDispatchInfo.Capture(bodyException).Throw();
            throw;
        }

        // The run can be claimed after the checks above, and the store then leaves the file as it is.
        if (result is ChunkWriteResult.Closed)
            throw UploadErrors.Closed();

        if (result is ChunkWriteResult.NoRoom)
            throw UploadErrors.StorageFull(StorageFullRetryAfter);

        if (result is not ChunkWriteResult.Written)
            throw UploadErrors.NotFound();

        _total.Start(record.Id);

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// Gets the status of an upload: <c>GET {path}/uploads/{id}</c>.
    /// </summary>
    public async Task GetStatusAsync(HttpContext context, UploadEndpoint endpoint)
    {
        string userId = await _user.GetUserIdAsync(context);
        var cancellationToken = context.RequestAborted;

        var (upload, _) = await FindAsync(context, endpoint, 2, userId, cancellationToken) ?? throw UploadErrors.NotFound();

        if (upload.RunState is UploadRunState.Lapsed && !_runs.ContainsKey(upload.Record.Id))
            upload = await InterruptAsync(upload) ?? throw UploadErrors.NotFound();

        var record = upload.Record;
        var expiresUtc = GetExpiresUtc(upload);
        UploadStatus status;

        if (upload.Outcome is not null)
        {
            status = new UploadStatus(UploadState.Completed, record.Length, record.ChunkSize, [], expiresUtc);
        }
        else if (upload.RunState is not UploadRunState.None || _runs.ContainsKey(record.Id))
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
    /// Aborts an upload: <c>DELETE {path}/uploads/{id}</c>. An upload that does not exist is not an error.
    /// </summary>
    public async Task AbortAsync(HttpContext context, UploadEndpoint endpoint)
    {
        string userId = await _user.GetUserIdAsync(context);

        if (await FindAsync(context, endpoint, 2, userId, context.RequestAborted) is var (upload, _))
        {
            if (_runs.TryGetValue(upload.Record.Id, out var run))
            {
                try
                {
                    run.Cancel(RunCancelReason.Aborted);
                }
                catch (Exception ex)
                {
                    // The upload is deleted all the same, which stops the handler when its claim's renewal fails.
                    _logger.LogError(ex, "A callback of the cancellation token of the handler of upload {UploadId} failed.", upload.Record.Id);
                }
            }

            await _store.DeleteAsync(upload.Record, CancellationToken.None);
            _total.End(upload.Record.Id);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// Removes the uploads that have expired and the outcomes that are no longer kept, ends the runs whose claims have lapsed, and computes the total size
    /// of the open uploads.
    /// </summary>
    public async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        var counted = new Dictionary<string, UploadLength>(StringComparer.Ordinal);
        var nowUtc = UtcNow;
        var createdBeforeCount = _total.StartCount();

        try
        {
            await CountAndCleanUpAsync(counted, nowUtc, createdBeforeCount, cancellationToken);
        }
        catch
        {
            // The total stays as it was until a count succeeds.
            _total.AbortCount();
            throw;
        }

        _total.EndCount(counted, createdBeforeCount);
        await _store.CleanUpAsync(cancellationToken);
    }

    /// <summary>
    /// Removes the uploads that have expired and the outcomes that are no longer kept, ends the runs whose claims have lapsed, and counts the lengths of
    /// the open uploads.
    /// </summary>
    private async Task CountAndCleanUpAsync(
        Dictionary<string, UploadLength> counted, DateTime nowUtc, HashSet<string> createdBeforeCount, CancellationToken cancellationToken)
    {
        await foreach (var upload in _store.ListAsync(null, cancellationToken: cancellationToken))
        {
            var record = upload.Record;

            try
            {
                if (upload.Outcome is not null)
                {
                    if (upload.OutcomeUtc + _options.OutcomeRetention <= nowUtc)
                        await DeleteAsync(record);
                }
                else if (upload.RunState is UploadRunState.Lapsed)
                {
                    if (!_runs.ContainsKey(record.Id))
                        await InterruptAsync(upload);
                }
                else if (upload.RunState is UploadRunState.None && GetReceivingExpiresUtc(upload) <= nowUtc && !_runs.ContainsKey(record.Id))
                {
                    await DeleteAsync(record);
                }
                else if (_total.IsCounted(record.Id, createdBeforeCount))
                {
                    counted[record.Id] = new UploadLength(record.Length, IsUnstarted(upload) ? GetReceivingExpiresUtc(upload) : null);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The cleanup goes on with the other uploads, and the next one tries again.
                _logger.LogError(ex, "Upload {UploadId} could not be cleaned up.", record.Id);
            }
        }
    }

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

    /// <summary>
    /// Counts the open uploads of a user, the uploads that are receiving chunks and the uploads whose handler is running, and their total length.
    /// </summary>
    private async ValueTask<(int Count, long Length)> CountOpenUploadsAsync(string userId, CancellationToken cancellationToken)
    {
        int count = 0;
        long length = 0;
        var nowUtc = UtcNow;

        await foreach (var upload in _store.ListAsync(userId, withoutOutcomes: true, cancellationToken))
        {
            if (!IsExpired(upload, nowUtc) && upload.Outcome is null && upload.RunState is not UploadRunState.Lapsed)
            {
                count++;
                length += upload.Record.Length;
            }
        }

        return (count, length);
    }

    /// <summary>
    /// Reserves the idempotency key of a new upload, or gets the upload that an earlier creation with the key created. While the earlier creation has not
    /// created its upload yet, it waits for it.
    /// </summary>
    /// <returns><see langword="null"/> if the key was reserved for the new upload, or the upload that the earlier creation created.</returns>
    /// <exception cref="ConflictApiException">The key's upload is for another endpoint, query string, file or command
    /// (<see cref="UploadErrorCodes.KeyConflict"/>).</exception>
    /// <exception cref="ServiceUnavailableApiException">The earlier creation has not created its upload, or its stale reservation could not be released,
    /// within the time the new one waits (<see cref="UploadErrorCodes.CreationPending"/>).</exception>
    private async ValueTask<StoredUpload?> ReserveKeyAsync(
        string userId, string key, string uploadId, UploadMetadata metadata, long length, CancellationToken cancellationToken)
    {
        long waitStart = _timeProvider.GetTimestamp();
        UploadKeyReservation? released = null;

        while (true)
        {
            // Each round checks the time first, so that every path through the loop ends the wait once its time has passed.
            if (_timeProvider.GetElapsedTime(waitStart) >= KeyWaitTimeout)
                throw UploadErrors.CreationPending(KeyRetryAfter);

            var reservation = await _store.ReserveKeyAsync(userId, key, uploadId, cancellationToken);

            if (reservation.UploadId == uploadId)
                return null;

            var upload = await _store.GetAsync(userId, reservation.UploadId, cancellationToken);

            if (upload is not null)
            {
                // Deleting an expired upload releases its key. A deletion that fails fails the creation, which the client makes again.
                if (IsExpired(upload, UtcNow))
                {
                    await _store.DeleteAsync(upload.Record, cancellationToken);
                    _total.End(upload.Record.Id);
                    continue;
                }

                if (UploadMetadata.Read(upload.Record.Metadata) is { } existing && IsSameUpload(existing, upload.Record.Length, metadata, length))
                    return upload;

                throw UploadErrors.KeyConflict();
            }

            // The upload is not created yet: the earlier creation is still running, or it stopped without releasing the key, which is then released and
            // reserved again at once. A release that didn't free the key, such as while the store can't remove the reservation, is made again after the
            // poll interval, and the wait ends as for a creation that is still running.
            if (reservation.ReservedUtc + StaleKeyAge > UtcNow)
            {
                await Task.Delay(KeyPollInterval, _timeProvider, cancellationToken);
                continue;
            }

            if (released is not null && released.UploadId == reservation.UploadId && released.ReservedUtc == reservation.ReservedUtc)
                await Task.Delay(KeyPollInterval, _timeProvider, cancellationToken);

            await _store.ReleaseKeyAsync(userId, key, reservation.UploadId, cancellationToken);
            released = reservation;
        }
    }

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

        // The path is escaped, since the header can't have characters that aren't ASCII, and a decoded character such as ? or % would change its meaning.
        string path = EscapePath(context.Request.PathBase.Add(context.Request.Path).Value ?? string.Empty);
        response.Headers.Location = $"{path.TrimEnd('/')}/{record.Id}";

        var session = new UploadSession(record.Id, record.ChunkSize, expiresUtc);
        await response.WriteAsJsonAsync(session, UploadJsonContext.Default.UploadSession, cancellationToken: cancellationToken);
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
    /// Gets the time an upload expires: while it is receiving chunks, the idle expiry after its last chunk; while its handler runs, the idle expiry after
    /// the run started; and once it has finished, the outcome retention after its outcome.
    /// </summary>
    private DateTime GetExpiresUtc(StoredUpload upload)
    {
        if (upload.OutcomeUtc is { } outcomeUtc)
            return outcomeUtc + _options.OutcomeRetention;

        if (upload.RunStartedUtc is { } runStartedUtc)
            return runStartedUtc + _options.IdleExpiry;

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

    private bool IsExpired(StoredUpload upload, DateTime nowUtc)
    {
        if (upload.OutcomeUtc is { } outcomeUtc)
            return outcomeUtc + _options.OutcomeRetention <= nowUtc;

        return upload.RunState is UploadRunState.None && GetReceivingExpiresUtc(upload) <= nowUtc && !_runs.ContainsKey(upload.Record.Id);
    }

    /// <summary>
    /// Finds the upload that a request is for, which must belong to the request's user, endpoint and route values, and must not have expired.
    /// </summary>
    private async ValueTask<(StoredUpload Upload, UploadMetadata Metadata)?> FindAsync(
        HttpContext context, UploadEndpoint endpoint, int ownSegmentCount, string userId, CancellationToken cancellationToken)
    {
        if (context.Request.RouteValues[IdRouteParameter] is not string uploadId || !ShortLivedId.IsWellFormed(uploadId))
            return null;

        var upload = await _store.GetAsync(userId, uploadId, cancellationToken);

        if (upload is null || upload.Record.Id != uploadId || upload.Record.UserId != userId)
            return null;

        if (UploadMetadata.Read(upload.Record.Metadata) is not { } metadata)
        {
            _logger.LogError("The metadata of an upload cannot be read.");
            return null;
        }

        if (metadata.Endpoint != endpoint.GetKey(context, ownSegmentCount) || !RouteValuesEqual(metadata.RouteValues, GetRouteValues(context)))
            return null;

        if (IsExpired(upload, UtcNow))
        {
            await DeleteAsync(upload.Record);
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
            _total.End(record.Id);

            _logger.LogWarning(
                "The run of the handler of upload {UploadId} was claimed {StartedUtc:O} and stopped without an outcome, so the upload is ended as " +
                "interrupted.",
                record.Id,
                upload.RunStartedUtc);
        }

        return await _store.GetAsync(record.UserId, record.Id, CancellationToken.None);
    }

    private async ValueTask DeleteAsync(UploadRecord record)
    {
        try
        {
            await _store.DeleteAsync(record, CancellationToken.None);
            _total.End(record.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload {UploadId} could not be deleted.", record.Id);
        }
    }
}
