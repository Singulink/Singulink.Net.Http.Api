#if !__WASM__ && !__ANDROID__ && !__IOS__
using System.Security.Cryptography;
using System.Text;
using Singulink.IO;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Keeps a session token in a file for <see cref="SessionTokenStore"/> in Windows App SDK apps, encrypted for the Windows user with DPAPI, so that a
/// backup restored onto another device or for another user can't use it. Other operating systems keep it unencrypted.
/// </summary>
internal static class FileSessionTokenStorage
{
    public static string? Read(IAbsoluteFilePath file)
    {
        byte[] stored;

        try
        {
            stored = File.ReadAllBytes(file.PathExport);
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
            file.Delete();
            return null;
        }
    }

    public static void Write(IAbsoluteFilePath file, string? token)
    {
        if (token is null)
        {
            if (file.Exists)
                file.Delete();

            return;
        }

        byte[] data = Encoding.UTF8.GetBytes(token);

        if (OperatingSystem.IsWindows())
            data = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

        file.ParentDirectory.Create();

        // The file is replaced as a whole, so that a write that fails keeps the token that was stored before.
        var tempFile = file.ParentDirectory.CombineFile(file.Name + ".tmp", PathOptions.None);
        File.WriteAllBytes(tempFile.PathExport, data);
        tempFile.MoveTo(file, overwrite: true);
    }
}
#endif
