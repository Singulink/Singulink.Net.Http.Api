using System.Text;
using System.Text.Json;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// The final outcome of an upload, which stores keep as text (see <see cref="IUploadStore.TrySetOutcomeAsync"/>): the last item of its handler, if it
/// produced one, or its error.
/// </summary>
internal sealed class UploadOutcome
{
    private static readonly byte[] NullJson = "null"u8.ToArray();

    public bool HasItem { get; init; }

    public JsonElement? Item { get; init; }

    public StreamErrorInfo? Error { get; init; }

    public static UploadOutcome Success(byte[]? itemJson)
    {
        if (itemJson is null)
            return new UploadOutcome();

        using var item = JsonDocument.Parse(itemJson);
        return new UploadOutcome { HasItem = true, Item = item.RootElement.Clone() };
    }

    public static UploadOutcome Failure(StreamErrorInfo error) => new() { Error = error };

    public static UploadOutcome? Read(string outcome)
    {
        try
        {
            return JsonSerializer.Deserialize(outcome, UploadStateJsonContext.Default.UploadOutcome);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the JSON of the item. A <see langword="null"/> item is the JSON <c>null</c>.
    /// </summary>
    public ReadOnlyMemory<byte> GetItemJson() => Item is { } item ? Encoding.UTF8.GetBytes(item.GetRawText()) : NullJson;

    public string Write() => JsonSerializer.Serialize(this, UploadStateJsonContext.Default.UploadOutcome);
}
