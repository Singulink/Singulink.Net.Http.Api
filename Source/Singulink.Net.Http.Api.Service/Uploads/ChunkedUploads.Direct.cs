using System.Buffers.Text;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <content>
/// Direct uploads, of files that fit in one chunk, in one request.
/// </content>
internal sealed partial class ChunkedUploads
{
    /// <summary>
    /// Uploads a file directly, in one request: <c>POST {path}/uploads/direct</c>, with the file as the body and the values of its creation in a header.
    /// Creates the upload, writes the body as its one chunk, and completes it, as the three requests of an upload do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response starts once the body is stored, with <c>201 Created</c>, or <c>200 OK</c> for a request made again that got the upload of an earlier one,
    /// and the upload's path in its <c>Location</c> header, so that a client whose response breaks off completes the upload by its ID.
    /// </para>
    /// <para>
    /// A request made again with the upload's ID gets the upload that the first one created: it writes its body again while the upload receives it, and
    /// attaches to the run otherwise. The body of an upload that has more than one chunk, of a direct upload made with the ID of an upload in chunks, which a
    /// client other than the toolkit's can make, is read and left out, and the completion then reports the chunks as missing. A request whose upload was
    /// cancelled before it arrived, such as one that a proxy held until it had the whole body, creates the upload, and the run then finds the cancellation
    /// and doesn't start the handler. A body that fails, such as of a
    /// client that disconnects, leaves the upload as a chunk that fails does, and the body's error is thrown.
    /// </para>
    /// </remarks>
    public async Task UploadDirectAsync(HttpContext context, UploadEndpoint endpoint)
    {
        var clientAborted = context.RequestAborted;
        ChunkContentStream? content = null;
        ChunkWriteResult? result = null;
        string userId;
        UploadRecord record;
        UploadMetadata metadata;
        bool isCreated;

        try
        {
            userId = await _user.GetUserIdAsync(context);

            var creation = ReadCreationHeader(context.Request);
            CheckCreation(creation);

            long length = creation.Length;

            if (context.Request.ContentLength is { } contentLength && contentLength != length)
                throw UploadErrors.BodyLengthInvalid();

            var limits = await GetLimitsAsync(context, userId, clientAborted);
            CheckLength(length, endpoint, limits);

            // A file that one request can't carry is uploaded in chunks. This comes after the check of its length against the endpoint's largest upload, so
            // that a file over it gets that error, and before the limits of the user's uploads, whose errors its creation then reports.
            if (length > MaxChunkSize)
                throw UploadErrors.DirectTooLarge(MaxChunkSize);

            // The file's length is the request's size limit, whatever the server's limit is.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maxRequestBodySize)
                maxRequestBodySize.MaxRequestBodySize = length;

            int chunkSize = (int)Math.Clamp(length, MinChunkSize, MaxChunkSize);
            (record, var existing, metadata) = await CreateOrGetAsync(context, endpoint, userId, creation, limits, chunkSize, 2, clientAborted);
            isCreated = existing is null;

            if (existing is null || !IsClosed(existing))
            {
                if (_releases.ContainsKey(UserUploadId.Of(record)))
                    throw UploadErrors.StoreUnavailable(GuardedUploadStore.RetryAfter);

                if (record.ChunkCount is 1)
                {
                    content = new ChunkContentStream(context.Request.Body, record.GetChunkLength(0));
                    result = await _store.WriteChunkAsync(record, 0, content, clientAborted);
                }
            }
        }
        catch (Exception ex) when (content is { IsLengthMismatch: true })
        {
            throw UploadErrors.BodyLengthInvalid(ex);
        }
        catch (Exception) when (content?.BodyException is { } bodyException)
        {
            ExceptionDispatchInfo.Capture(bodyException).Throw();
            throw;
        }
        catch (Exception) when (!clientAborted.IsCancellationRequested)
        {
            // A rejection before the body is stored reads the body first (see DiscardBodyAsync). A body that failed is handled above, and isn't read again.
            await DiscardBodyAsync(context);
            throw;
        }

        // A body that is not stored is read and left out: the upload's run has started or was claimed in the meantime, such as by the same request made again,
        // and the completion attaches to it, or the upload has other chunks, or the store has no room for it or the upload is gone, which is answered below.
        if (result is not ChunkWriteResult.Written)
            await DiscardBodyAsync(context);

        if (result is ChunkWriteResult.NoRoom)
            throw UploadErrors.StorageFull(StorageFullRetryAfter);

        if (result is ChunkWriteResult.NotFound)
            throw UploadErrors.NotFound();

        var serializerOptions = GetSerializerOptions(context);
        context.Response.Headers.Location = GetUploadPath(context, record.Id, segmentsAfterUploads: 1);

        using var writer = CreateCompletionWriter(context, endpoint, serializerOptions, isCreated ? StatusCodes.Status201Created : StatusCodes.Status200OK);

        // The response starts with a ping, so that the client has the upload's path before anything can fail.
        await writer.WritePingAsync();
        await RunCompletionAsync(context, endpoint, writer, serializerOptions, ct => FindAsync(record.Id, metadata.Endpoint, metadata.RouteValues, userId, ct));
    }

    /// <summary>
    /// Gets the values of the creation of a direct upload from its header, which has the JSON of a creation in UTF-8, base64url-encoded.
    /// </summary>
    private static CreateUploadCommand ReadCreationHeader(HttpRequest request)
    {
        if (request.Headers[ChunkedUploadProtocol.CreationHeaderName] is not [{ } value])
            throw UploadErrors.CreationUnreadable();

        if (value.Length > ChunkedUploadProtocol.MaxCreationHeaderLength)
            throw UploadErrors.CreationHeaderTooLarge(ChunkedUploadProtocol.MaxCreationHeaderLength);

        try
        {
            return JsonSerializer.Deserialize(Base64Url.DecodeFromChars(value), UploadJsonContext.Default.CreateUploadCommand) ??
                throw UploadErrors.CreationUnreadable();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw UploadErrors.CreationUnreadable(ex);
        }
    }
}
