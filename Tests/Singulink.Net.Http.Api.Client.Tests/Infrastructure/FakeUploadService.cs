using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The service side of the chunked upload protocol for one upload endpoint, in memory, as an HTTP message handler, so that the uploads of clients can be
/// tested without a server. Tests can answer or fail any request in its place, before or after the fake handles it.
/// </summary>
public sealed class FakeUploadService : HttpMessageHandler
{
    public const string EndRecord = """{"end":true}""";

    public const string PingRecord = """{"ping":true}""";

    private static readonly DateTime ExpiresUtc = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions ItemJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, FakeUpload> _uploads = new();
    private readonly ConcurrentDictionary<string, FakeUpload> _uploadsByKey = new();
    private readonly List<UploadRequest> _requests = [];
    private int _uploadCount;

    public FakeUploadService(string endpointPath = "files")
    {
        EndpointPath = endpointPath;
        Complete = completion => Responses.Stream(ItemRecord(1, completion.Upload.Text), EndRecord);
    }

    /// <summary>
    /// Gets the path of the upload endpoint, relative to the client's base address.
    /// </summary>
    public string EndpointPath { get; }

    /// <summary>
    /// Gets or sets the chunk size of uploads that don't ask for one. Uploads that ask for one get it.
    /// </summary>
    public int DefaultChunkSize { get; set; } = 4;

    /// <summary>
    /// Gets or sets a function that answers a request in the fake's place, or returns <see langword="null"/> to let the fake handle it. It can throw to
    /// fail the request, as a failed connection does, and it runs with the request's cancellation token.
    /// </summary>
    public Func<UploadRequest, CancellationToken, Task<HttpResponseMessage?>>? Before { get; set; }

    /// <summary>
    /// Gets or sets a function that replaces the fake's response to a request that it handled, or throws in its place, as for a response that was lost.
    /// </summary>
    public Func<UploadRequest, HttpResponseMessage, HttpResponseMessage>? After { get; set; }

    /// <summary>
    /// Gets or sets the function that produces the response of a completion that has every chunk. By default it streams one item with the upload's
    /// content as its name, and the end record.
    /// </summary>
    public Func<FakeCompletion, HttpResponseMessage> Complete { get; set; }

    public IReadOnlyList<UploadRequest> Requests
    {
        get {
            lock (_requests)
                return [.. _requests];
        }
    }

    public IReadOnlyList<FakeUpload> Uploads => [.. _uploads.Values];

    public FakeUpload SingleUpload => Uploads.ShouldHaveSingleItem();

    public IEnumerable<UploadRequest> RequestsOf(UploadRequestKind kind) => Requests.Where(r => r.Kind == kind);

    public static string ItemRecord(int id, string name) => JsonSerializer.Serialize(new { item = new TestItem(id, name) }, ItemJsonOptions);

    public static string ErrorRecord(int status, string? code, string message) =>
        JsonSerializer.Serialize(new { error = new { status, code, message } }, ItemJsonOptions);

    public static string ErrorRecord(int status, string? code, string message, int retryAfter) =>
        JsonSerializer.Serialize(new { error = new { status, code, message, retryAfter } }, ItemJsonOptions);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uploadRequest = await UploadRequest.CaptureAsync(request, EndpointPath, cancellationToken);

        lock (_requests)
            _requests.Add(uploadRequest);

        HttpResponseMessage? response = null;

        if (Before is { } before)
            response = await before(uploadRequest, cancellationToken);

        if (response is null)
        {
            response = Handle(uploadRequest);

            if (After is { } after)
                response = after(uploadRequest, response);
        }

        response.RequestMessage = request;
        return response;
    }

    private HttpResponseMessage Handle(UploadRequest request)
    {
        if (request.Kind is UploadRequestKind.Create)
            return Create(request);

        if (request.UploadId is null || !_uploads.TryGetValue(request.UploadId, out var upload) || upload.IsAborted)
        {
            if (request.Kind is UploadRequestKind.Abort)
                return Responses.NoContent();

            return Error(new NotFoundApiException("The upload does not exist.") { ErrorCode = UploadErrorCodes.NotFound });
        }

        switch (request.Kind)
        {
            case UploadRequestKind.Chunk:
                return ReceiveChunk(upload, request);
            case UploadRequestKind.Status:
                return Json(new UploadStatus(
                    upload.IsCompleted ? UploadState.Completed : UploadState.Receiving,
                    upload.Length,
                    upload.ChunkSize,
                    upload.GetMissingChunks(),
                    ExpiresUtc), UploadTestJsonContext.Default.UploadStatus, HttpStatusCode.OK);
            case UploadRequestKind.Complete:
                if (upload.GetMissingChunks().Count > 0)
                    return Error(new ConflictApiException("Chunks of the upload are missing.") { ErrorCode = UploadErrorCodes.Incomplete });

                upload.IsCompleted = true;
                int index = Interlocked.Increment(ref upload.CompletionCount);

                return Complete(new FakeCompletion(upload, index, request));
            case UploadRequestKind.Abort:
                upload.IsAborted = true;
                return Responses.NoContent();
            default:
                return Responses.Error(HttpStatusCode.NotFound, "Not found.");
        }
    }

    private HttpResponseMessage Create(UploadRequest request)
    {
        var command = JsonSerializer.Deserialize(request.Body!, UploadTestJsonContext.Default.CreateUploadCommand).ShouldNotBeNull();

        // A creation made again with the key of an earlier one gets its upload, as the service does.
        if (command.IdempotencyKey is { } key && _uploadsByKey.TryGetValue(key, out var existing))
            return Json(new UploadSession(existing.Id, existing.ChunkSize, ExpiresUtc), UploadTestJsonContext.Default.UploadSession, HttpStatusCode.OK);

        string id = $"upload-{Interlocked.Increment(ref _uploadCount)}/+";
        var upload = new FakeUpload(id, command, command.ChunkSize ?? DefaultChunkSize);
        _uploads[id] = upload;

        if (command.IdempotencyKey is { } newKey)
            _uploadsByKey[newKey] = upload;

        var response = Json(new UploadSession(id, upload.ChunkSize, ExpiresUtc), UploadTestJsonContext.Default.UploadSession, HttpStatusCode.Created);
        response.Headers.Location = new Uri(request.Uri, $"uploads/{Uri.EscapeDataString(id)}");

        return response;
    }

    private static HttpResponseMessage ReceiveChunk(FakeUpload upload, UploadRequest request)
    {
        if (upload.IsCompleted)
            return Error(new ConflictApiException("The upload is closed.") { ErrorCode = UploadErrorCodes.Closed });

        if (request.ContentRange is not { From: { } from, To: { } to, Length: { } length } || length != upload.Length || from % upload.ChunkSize != 0 ||
            to - from + 1 != upload.GetChunkLength((int)(from / upload.ChunkSize)) || request.BodyBytes.Length != to - from + 1 ||
            request.ContentType != "application/octet-stream")
        {
            return Error(new BadRequestApiException("The chunk is not valid.") { ErrorCode = UploadErrorCodes.ChunkInvalid });
        }

        upload.Receive((int)(from / upload.ChunkSize), request.BodyBytes);
        return Responses.NoContent();
    }

    private static HttpResponseMessage Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, HttpStatusCode status) =>
        new(status) { Content = JsonContent.Create(value, typeInfo) };

    private static HttpResponseMessage Error(ApiException exception) => ApiErrorResponse.Create(exception);
}

public enum UploadRequestKind
{
    Create,
    Chunk,
    Status,
    Complete,
    Abort,
    Other,
}

/// <summary>
/// A request that a <see cref="FakeUploadService"/> received.
/// </summary>
public sealed record UploadRequest(
    UploadRequestKind Kind,
    HttpMethod Method,
    Uri Uri,
    string? UploadId,
    string? Cookie,
    string? ContentType,
    System.Net.Http.Headers.ContentRangeHeaderValue? ContentRange,
    byte[] BodyBytes)
{
    public string? Body => BodyBytes.Length is 0 && ContentType is null ? null : Encoding.UTF8.GetString(BodyBytes);

    public string Query => Uri.Query;

    public static async Task<UploadRequest> CaptureAsync(HttpRequestMessage request, string endpointPath, CancellationToken cancellationToken)
    {
#if NET
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
#else
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync();
#endif

        // The escaped path, so that an escaped slash in an upload ID doesn't split it.
        string path = request.RequestUri!.AbsolutePath[TestApiClient.DefaultBaseAddress.AbsolutePath.Length..];
        string uploadsPath = $"{endpointPath}/uploads";
        string? uploadId = null;
        var kind = UploadRequestKind.Other;

        if (path == uploadsPath && request.Method == HttpMethod.Post)
        {
            kind = UploadRequestKind.Create;
        }
        else if (path.StartsWith(uploadsPath + "/", StringComparison.Ordinal))
        {
            string[] segments = path[(uploadsPath.Length + 1)..].Split('/');
            uploadId = Uri.UnescapeDataString(segments[0]);

            kind = (segments.Length, request.Method.Method) switch {
                (1, "PUT") => UploadRequestKind.Chunk,
                (1, "GET") => UploadRequestKind.Status,
                (1, "DELETE") => UploadRequestKind.Abort,
                (2, "POST") when segments[1] is "complete" => UploadRequestKind.Complete,
                _ => UploadRequestKind.Other,
            };
        }

        return new UploadRequest(
            kind,
            request.Method,
            request.RequestUri,
            uploadId,
            request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content?.Headers.ContentRange,
            body);
    }
}

/// <summary>
/// An upload that a <see cref="FakeUploadService"/> created.
/// </summary>
public sealed class FakeUpload
{
    private readonly bool[] _received;
    private readonly byte[] _data;

    public int CompletionCount;

    public FakeUpload(string id, CreateUploadCommand command, int chunkSize)
    {
        Id = id;
        Command = command;
        ChunkSize = chunkSize;
        _data = new byte[command.Length];
        _received = new bool[(command.Length + chunkSize - 1) / chunkSize];
    }

    public string Id { get; }

    public CreateUploadCommand Command { get; }

    public long Length => Command.Length;

    public int ChunkSize { get; }

    public int ChunkCount => _received.Length;

    public bool IsAborted { get; set; }

    public bool IsCompleted { get; set; }

    public byte[] Data
    {
        get {
            lock (_data)
                return [.. _data];
        }
    }

    public string Text => Encoding.UTF8.GetString(Data);

    public int GetChunkLength(int index) => (int)Math.Min(ChunkSize, Length - ((long)index * ChunkSize));

    /// <summary>
    /// Forgets that a chunk arrived, as a service that lost it does.
    /// </summary>
    public void MarkMissing(int index)
    {
        lock (_data)
            _received[index] = false;
    }

    public void Receive(int index, byte[] bytes)
    {
        lock (_data)
        {
            bytes.CopyTo(_data, (long)index * ChunkSize);
            _received[index] = true;
        }
    }

    public List<ChunkRange> GetMissingChunks()
    {
        var ranges = new List<ChunkRange>();

        lock (_data)
        {
            for (int i = 0; i < _received.Length; i++)
            {
                if (_received[i])
                    continue;

                int first = i;

                while (i + 1 < _received.Length && !_received[i + 1])
                    i++;

                ranges.Add(new ChunkRange(first, i));
            }
        }

        return ranges;
    }
}

/// <summary>
/// A completion request of an upload that has every chunk.
/// </summary>
/// <param name="Upload">The upload.</param>
/// <param name="Index">The number of the completion request of the upload, from 1.</param>
/// <param name="Request">The request.</param>
public sealed record FakeCompletion(FakeUpload Upload, int Index, UploadRequest Request);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CreateUploadCommand))]
[JsonSerializable(typeof(UploadSession))]
[JsonSerializable(typeof(UploadStatus))]
internal sealed partial class UploadTestJsonContext : JsonSerializerContext;
