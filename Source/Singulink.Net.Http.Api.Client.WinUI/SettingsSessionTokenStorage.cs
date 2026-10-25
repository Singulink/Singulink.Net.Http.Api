#if !__WASM__ && !WINDOWS && !__ANDROID__ && !__IOS__
using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Keeps a session token in an app's settings for <see cref="SessionTokenStore"/> on desktop. On Windows it is encrypted for the Windows user with DPAPI,
/// so that a backup restored onto another device or for another user can't use it. Linux and macOS keep it unencrypted.
/// </summary>
internal static class SettingsSessionTokenStorage
{
    public static string? Read(IDictionary<string, object> settings, string key)
    {
        if (!settings.TryGetValue(key, out object? value) || value is not string stored)
            return null;

        if (!OperatingSystem.IsWindows())
            return stored;

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // A token that isn't encrypted for this user, such as one stored unencrypted or restored from another device, can't be used.
            settings.Remove(key);
            return null;
        }
    }

    public static void Write(IDictionary<string, object> settings, string key, string? token)
    {
        if (token is null)
        {
            settings.Remove(key);
            return;
        }

        if (OperatingSystem.IsWindows())
            token = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

        settings[key] = token;
    }
}
#endif
