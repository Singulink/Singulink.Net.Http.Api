using System.Text.Json;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// The creation that the <see cref="UploadRequest"/> parameters of an upload's validator are bound from, kept in the items of the creation's request.
/// </summary>
internal sealed class UploadRequestBinding(UploadRequest request, JsonElement? command, JsonSerializerOptions serializerOptions)
{
    private static readonly object Key = new();

    public UploadRequest Request { get; } = request;

    public JsonElement? Command { get; } = command;

    public JsonSerializerOptions SerializerOptions { get; } = serializerOptions;

    public static UploadRequestBinding Get(HttpContext context)
    {
        return context.Items.TryGetValue(Key, out object? value) && value is UploadRequestBinding binding ? binding : throw new InvalidOperationException(
            $"{nameof(UploadRequest)} parameters are only bound in the validators of chunked uploads (see MapChunkedUpload).");
    }

    public void Set(HttpContext context) => context.Items[Key] = this;
}
