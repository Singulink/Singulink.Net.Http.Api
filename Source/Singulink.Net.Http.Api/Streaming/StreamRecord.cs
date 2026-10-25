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

    /// <summary>
    /// Gets or sets the reference ID of the error, in the <c>D</c> format of <see cref="Guid"/>. A value that is not a GUID is ignored.
    /// </summary>
    public string? ReferenceId { get; set; }

    /// <summary>
    /// Gets or sets how long the client should wait before trying again, in whole seconds. A negative value, or one longer than <see cref="TimeSpan"/> can
    /// hold, is ignored.
    /// </summary>
    public long? RetryAfter { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StreamRecord))]
internal sealed partial class StreamJsonContext : JsonSerializerContext;
