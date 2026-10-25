using System.Text.Json;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// The service's metadata of an upload, which stores keep as the text of <see cref="UploadRecord.Metadata"/>: the endpoint and route values the upload
/// belongs to, and the query string, the file's name, content type and the app's command from the creation.
/// </summary>
internal sealed class UploadMetadata
{
    public required string Endpoint { get; init; }

    public required Dictionary<string, string?> RouteValues { get; init; }

    /// <summary>
    /// Gets the query string parameters of the creation, without the user ID precondition, which the completion must have too.
    /// </summary>
    public required Dictionary<string, string?[]> Query { get; init; }

    public required string FileName { get; init; }

    public string? ContentType { get; init; }

    public JsonElement? Command { get; init; }

    public static UploadMetadata? Read(string metadata)
    {
        try
        {
            return JsonSerializer.Deserialize(metadata, UploadStateJsonContext.Default.UploadMetadata);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Write() => JsonSerializer.Serialize(this, UploadStateJsonContext.Default.UploadMetadata);
}
