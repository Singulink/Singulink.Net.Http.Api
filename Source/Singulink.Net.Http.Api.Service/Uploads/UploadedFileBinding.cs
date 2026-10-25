using System.Text.Json;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// The upload that the <see cref="UploadedFile"/> parameters of an upload's handler are bound from, kept in the items of the completion's request.
/// </summary>
internal sealed class UploadedFileBinding(UploadedFile file, UploadContent content, JsonElement? command, JsonSerializerOptions serializerOptions)
{
    private static readonly object Key = new();

    public UploadedFile File { get; } = file;

    public UploadContent Content { get; } = content;

    public JsonElement? Command { get; } = command;

    public JsonSerializerOptions SerializerOptions { get; } = serializerOptions;

    public static UploadedFileBinding Get(HttpContext context)
    {
        return context.Items.TryGetValue(Key, out object? value) && value is UploadedFileBinding binding ? binding : throw new InvalidOperationException(
            $"{nameof(UploadedFile)} parameters are only bound in the handlers of chunked uploads (see MapChunkedUpload).");
    }

    public void Set(HttpContext context) => context.Items[Key] = this;
}
