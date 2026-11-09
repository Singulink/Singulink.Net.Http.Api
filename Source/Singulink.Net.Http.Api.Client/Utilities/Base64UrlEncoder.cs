namespace Singulink.Net.Http.Api.Client.Utilities;

/// <summary>
/// Encodes bytes as base64url without padding.
/// </summary>
internal static class Base64UrlEncoder
{
    public static string Encode(byte[] bytes)
    {
#if NET9_0_OR_GREATER
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
#else
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
#endif
    }
}
