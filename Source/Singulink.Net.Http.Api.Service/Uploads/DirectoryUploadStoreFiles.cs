using System.Text.Json;
using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1649 // File name should match first type name

/// <summary>
/// The record of an upload in its <c>upload.json</c> file.
/// </summary>
internal sealed class RecordFile
{
    public required string Id { get; init; }

    public required string UserId { get; init; }

    public required long Length { get; init; }

    public required int ChunkSize { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required string Metadata { get; init; }
}

/// <summary>
/// The claim of an upload's run in its <c>claim</c> file, with the time it was last renewed.
/// </summary>
internal sealed class ClaimFile
{
    public required string ClaimId { get; init; }

    public required TimeSpan LeaseDuration { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required DateTime RenewedUtc { get; init; }
}

/// <summary>
/// The marker of a write of a chunk, with the time the write last showed that it is alive.
/// </summary>
internal sealed class WritingFile
{
    public required DateTime AliveUtc { get; init; }
}

/// <summary>
/// The outcome of a finished upload with its record, in the <c>finished.json</c> file of its directory and then in the user's outcomes.
/// </summary>
internal sealed class OutcomeFile
{
    public required RecordFile Record { get; init; }

    public required string Outcome { get; init; }

    public required DateTime StoredUtc { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RecordFile))]
[JsonSerializable(typeof(ClaimFile))]
[JsonSerializable(typeof(WritingFile))]
[JsonSerializable(typeof(OutcomeFile))]
internal sealed partial class DirectoryUploadStoreJsonContext : JsonSerializerContext;
