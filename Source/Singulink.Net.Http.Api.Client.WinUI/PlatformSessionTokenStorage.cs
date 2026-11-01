#if !__WASM__ && !WINDOWS
using Windows.Security.Credentials;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Creates the iOS and Android storage of a <see cref="SessionTokenStore"/>. Decided at run time in every build, since apps with Uno Platform's Skia
/// renderer load the plain .NET build on iOS and Android.
/// </summary>
internal static class PlatformSessionTokenStorage
{
    /// <summary>
    /// Creates the storage for the current platform, or returns <see langword="null"/> if the platform has none.
    /// </summary>
    public static ISessionTokenStorage? Create(string name) => Create(name, SessionTokenStoreNative.IsSupported, OperatingSystem.IsAndroid());

    /// <summary>
    /// Creates the storage: the keychain if it is supported (iOS), otherwise the password vault on Android, otherwise none.
    /// </summary>
    public static ISessionTokenStorage? Create(string name, bool isKeychainSupported, bool isAndroid) =>
        isKeychainSupported ? new KeychainSessionTokenStorage(name) : isAndroid ? new PasswordVaultSessionTokenStorage(name) : null;
}

/// <summary>
/// Keeps the token of a <see cref="SessionTokenStore"/> in an iOS keychain item that only this device can read, while it is unlocked
/// (see <see cref="SessionTokenStoreNative"/>).
/// </summary>
internal sealed class KeychainSessionTokenStorage(string name) : ISessionTokenStorage
{
    public string? Read() => SessionTokenStoreNative.Read(name);

    public void Write(string? token) => SessionTokenStoreNative.Write(name, token);
}

#pragma warning disable Uno0001 // The password vault is only implemented on Android and iOS. This storage is only used on Android, where apps with the Skia
                                // renderer get Uno Platform's Android implementation at run time with the plain .NET build of this library.

/// <summary>
/// Keeps the token of a <see cref="SessionTokenStore"/> in the app's password vault on Android, which encrypts it with an Android Keystore key.
/// </summary>
internal sealed class PasswordVaultSessionTokenStorage : ISessionTokenStorage
{
    // The password vault resource, which is the app's own.
    private const string Resource = "Singulink.Net.Http.Api.SessionTokens";

    private readonly string _name;
    private readonly Func<PasswordVault> _createVault;

    public PasswordVaultSessionTokenStorage(string name) : this(name, () => new PasswordVault())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasswordVaultSessionTokenStorage"/> class with the specified vault. Used by tests.
    /// </summary>
    public PasswordVaultSessionTokenStorage(string name, Func<PasswordVault> createVault)
    {
        _name = name;
        _createVault = createVault;
    }

    public string? Read()
    {
        // Uno's vault reads as empty if it can't be read, such as when a backup restored it onto another device.
        if (FindCredential(_createVault()) is not { } credential)
            return null;

        credential.RetrievePassword();
        return credential.Password;
    }

    public void Write(string? token)
    {
        var vault = _createVault();

        // Uno's vault replaces a credential with the same resource and user name.
        if (token is not null)
            vault.Add(new PasswordCredential(Resource, _name, token));
        else if (FindCredential(vault) is { } credential)
            vault.Remove(credential);
    }

    // The vault compares resources and user names ignoring case.
    private PasswordCredential? FindCredential(PasswordVault vault) => vault.RetrieveAll().FirstOrDefault(c =>
        string.Equals(c.Resource, Resource, StringComparison.OrdinalIgnoreCase) && string.Equals(c.UserName, _name, StringComparison.OrdinalIgnoreCase));
}

#pragma warning restore Uno0001

#endif
