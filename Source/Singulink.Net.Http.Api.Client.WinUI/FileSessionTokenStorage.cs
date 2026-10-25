#if !__WASM__ && !__ANDROID__ && !__IOS__
using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Keeps a session token in a file for <see cref="SessionTokenStore"/> in Windows App SDK apps, encrypted for the Windows user with DPAPI, so that a
/// backup restored onto another device or for another user can't use it. Other operating systems keep it unencrypted.
/// </summary>
internal static class FileSessionTokenStorage
{
    public static string? Read(string path)
    {
        byte[] stored;

        try
        {
            stored = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
            return Encoding.UTF8.GetString(stored);

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // A token that isn't encrypted for this user, such as one restored from another device, can't be used.
            File.Delete(path);
            return null;
        }
    }

    public static void Write(string path, string? token)
    {
        if (token is null)
        {
            if (File.Exists(path))
                File.Delete(path);

            return;
        }

        byte[] data = Encoding.UTF8.GetBytes(token);

        if (OperatingSystem.IsWindows())
            data = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // The file is replaced as a whole, so that a write that fails keeps the token that was stored before.
        string tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, data);
        File.Move(tempPath, path, overwrite: true);
    }
}
#endif
