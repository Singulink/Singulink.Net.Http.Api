using System.Text.Json;
using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api;

/// <summary>
/// A single line of a streaming response. Exactly one of the properties is set.
/// </summary>
internal sealed class StreamRecord
{
    /// <summary>
    /// Gets or sets the item. <see cref="JsonValueKind.Undefined"/> when the record is not an item record; <see cref="JsonValueKind.Null"/> for a null item.
    /// </summary>
    public JsonElement Item { get; set; }

    public bool? Ping { get; set; }

    public StreamErrorInfo? Error { get; set; }

    public bool? End { get; set; }
}

/// <summary>
/// Error information carried by a streaming response error record.
/// </summary>
internal sealed class StreamErrorInfo
{
    public int Status { get; set; }

    public string? Code { get; set; }

    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StreamRecord))]
internal sealed partial class StreamJsonContext : JsonSerializerContext;
