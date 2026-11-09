using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// The IDs of uploads: GUIDs in their 36-character form, in lowercase, which clients choose or the service creates, and the IDs of uploads that 4.x created.
/// </summary>
internal static class UploadIds
{
    private const string KeyIdPrefix = "httpapi:upload-key:";

    /// <summary>
    /// Formats an ID that a client chose.
    /// </summary>
    public static string Format(Guid id) => id.ToString("D");

    /// <summary>
    /// Creates the ID of an upload whose creation has none, such as of a 4.x client without an idempotency key.
    /// </summary>
    public static string Create() => Format(Guid.NewGuid());

    /// <summary>
    /// Gets the ID of an upload that a 4.x client creates with an idempotency key: a GUID of version 8 from a hash of the key, so that a creation made again
    /// with the key gets the upload that the first one created, as one made again with an ID does.
    /// </summary>
    public static string FromKey(string key)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(KeyIdPrefix + key), hash);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return Format(new Guid(hash[..16], bigEndian: true));
    }

    /// <summary>
    /// Gets the ID of an upload from the ID in a request's path: a GUID in its 36-character form, in any case, or the ID of an upload that 4.x created.
    /// </summary>
    /// <returns><see langword="true"/> if the value is an ID; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out string? uploadId)
    {
        if (value is { Length: 36 } && Guid.TryParseExact(value, "D", out var id))
        {
            uploadId = Format(id);
            return true;
        }

        // TODO 6.0: remove the IDs of uploads that 4.x created, which then no longer exist.
        uploadId = ShortLivedId.IsWellFormed(value) ? value : null;
        return uploadId is not null;
    }

    /// <summary>
    /// Gets a value indicating whether a value has the form of an upload's ID as the service gives it to a store: a GUID in its 36-character form, in
    /// lowercase, or the ID of an upload that 4.x created.
    /// </summary>
    public static bool IsWellFormed(ReadOnlySpan<char> value)
    {
        if (value.Length is 36)
        {
            Span<char> formatted = stackalloc char[36];
            return Guid.TryParseExact(value, "D", out var id) && id.TryFormat(formatted, out _, "D") && value.SequenceEqual(formatted);
        }

        // TODO 6.0: remove the IDs of uploads that 4.x created, as in TryParse.
        return ShortLivedId.IsWellFormed(value);
    }
}
