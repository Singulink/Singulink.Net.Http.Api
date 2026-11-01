namespace Singulink.Net.Http.Api.Client;

// Compatibility rule: Singulink.Net.Http.Api.Client.WinUI calls these internal members across packages, and an app can get a newer version of this
// package than of the WinUI package. Members are only ever added, never changed or removed (see the project file).

/// <summary>
/// Keeps session tokens in keychain items on iOS that only this device can read, while it is unlocked, for <c>SessionTokenStore</c> in
/// <c>Singulink.Net.Http.Api.Client.WinUI</c>. Other platforms aren't supported (see <see cref="IsSupported"/>).
/// </summary>
/// <remarks>
/// A token is written by changing the stored item in place, so that a write that fails keeps the token that was stored before.
/// </remarks>
internal static partial class SessionTokenStoreNative
{
    // The keychain service, which is the app's own.
    private const string StoreName = "Singulink.Net.Http.Api.SessionTokens";

    /// <summary>
    /// Gets a value indicating whether this platform is supported, which is iOS.
    /// </summary>
    public static bool IsSupported => IsSupportedCore;

    /// <summary>
    /// Reads the token stored under the specified name, the account of the keychain item. Returns <see langword="null"/> if there is none.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform is not supported.</exception>
    /// <exception cref="InvalidOperationException">The token could not be read, such as while the device is locked.</exception>
    public static string? Read(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureSupported();

        return ReadCore(name);
    }

    /// <summary>
    /// Stores the token under the specified name, the account of the keychain item, or removes the stored one if it is <see langword="null"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform is not supported.</exception>
    /// <exception cref="InvalidOperationException">The token could not be stored or removed, such as while the device is locked.</exception>
    public static void Write(string name, string? token)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureSupported();

        WriteCore(name, token);
    }

    private static void EnsureSupported()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Native session token storage is only supported on iOS.");
    }

#if !__IOS__
    private static bool IsSupportedCore => false;

    private static string? ReadCore(string name) => throw new PlatformNotSupportedException();

    private static void WriteCore(string name, string? token) => throw new PlatformNotSupportedException();
#endif
}
