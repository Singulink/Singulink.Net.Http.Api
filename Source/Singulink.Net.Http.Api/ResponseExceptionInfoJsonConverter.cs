using System.Text.Json;
using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api;

/// <summary>
/// Represents an AOT-safe, future proof JSON converter for <see cref="ResponseExceptionInfo" /> instances.
/// </summary>
public sealed partial class ResponseExceptionInfoJsonConverter : JsonConverter<ResponseExceptionInfo>
{
    private sealed record ResponseExceptionInfoDto(int StatusCode, string Message, string ContentType);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ResponseExceptionInfoDto))]
    private partial class ResponseExceptionInfoDtoJsonContext : JsonSerializerContext;

    /// <inheritdoc />
    public override ResponseExceptionInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dto = JsonSerializer.Deserialize(ref reader, ResponseExceptionInfoDtoJsonContext.Default.ResponseExceptionInfoDto);

        if (dto is null)
            return null;

        return new()
        {
            StatusCode = dto.StatusCode,
            Message = dto.Message,
            ContentType = dto.ContentType,
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ResponseExceptionInfo value, JsonSerializerOptions options)
    {
        ResponseExceptionInfoDto dto = new(value.StatusCode, value.Message, value.ContentType);
        JsonSerializer.Serialize(writer, dto, ResponseExceptionInfoDtoJsonContext.Default.ResponseExceptionInfoDto);
    }
}
