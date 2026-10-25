using System.Buffers;
#if __ANDROID__
using Windows.Security.Credentials;
#elif __IOS__
using Foundation;
using Security;
#elif WINDOWS
using System.Reflection;
#elif !__WASM__
using Windows.Storage;
#endif

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Keeps the token of a persistent session between app launches, for the session token and the session token changed callback of an API client: on
/// Android in the app's password vault, which encrypts it with an Android Keystore key; on iOS in a keychain item that only this device can read, while it
/// is unlocked; on Uno Platform desktop in the app's local settings, encrypted for the Windows user with DPAPI on Windows and unencrypted on Linux and
/// macOS; and in Windows App SDK apps, packaged or not, in a file encrypted for the Windows user with DPAPI, by default in a folder named after the app's
/// entry assembly in the user's local application data folder (see <see cref="FolderPath"/>). WebAssembly apps keep no token, since the browser holds the
/// session cookie: <see cref="Read"/> returns <see langword="null"/> and <see cref="Write(string?)"/> does nothing.
/// </summary>
/// <remarks>
/// <para>
/// Reading and writing are best effort: a token that can't be read or written, such as in a keychain while the device is locked, is reported to the
/// error handler instead of throwing, so that the session goes on in memory. A stored token that can't be decrypted, such as one that a backup carried to
/// another device or Windows user, reads as <see langword="null"/> and is removed.
/// </para>
/// <para>
/// On iOS, a token is written by changing the stored item in place, so that a write that fails keeps the token that was stored before.
/// </para>
/// </remarks>
public sealed class SessionTokenStore
{
    private const string DefaultName = "SessionToken";

#if __ANDROID__ || __IOS__
    // The password vault resource on Android, and the keychain service on iOS. Both are the app's own.
    private const string StoreName = "Singulink.Net.Http.Api.SessionTokens";
#endif

    // The characters that Windows doesn't allow in file names, on every platform (Path.GetInvalidFileNameChars depends on the platform).
    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create([.. Enumerable.Range(0, 32).Select(c => (char)c), .. "\"*/:<>?\\|"]);

    // The names that Windows reserves for devices, also with an extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private readonly Action<Exception>? _errorHandler;

#if !__WASM__ && !WINDOWS && !__ANDROID__ && !__IOS__
    private readonly Func<IDictionary<string, object>> _getSettings;
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionTokenStore"/> class.
    /// </summary>
    /// <param name="name">The name that the token is stored under: the user name in the password vault on Android, which compares names ignoring case,
    /// the account of the keychain item on iOS, the key in the local settings on Uno Platform desktop, and the name of the file in Windows App SDK apps, with
    /// a <c>.dat</c> extension. On every platform, it must be a valid file name on Windows: without control characters or any of
    /// <c>\ / : * ? " &lt; &gt; |</c>, and not a name that Windows reserves for a device, such as <c>CON</c> or <c>NUL</c>. Default is
    /// <c>"SessionToken"</c>.</param>
    /// <param name="errorHandler">A callback that receives the exceptions of reads and writes that failed. Default is <see langword="null"/>, which ignores
    /// them.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or not a valid file name on Windows.</exception>
    public SessionTokenStore(string name = DefaultName, Action<Exception>? errorHandler = null)
#if !__WASM__ && !WINDOWS && !__ANDROID__ && !__IOS__
        : this(name, errorHandler, () => ApplicationData.Current.LocalSettings.Values)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionTokenStore"/> class that keeps the token in the specified settings on desktop.
    /// </summary>
    internal SessionTokenStore(string name, Action<Exception>? errorHandler, Func<IDictionary<string, object>> getSettings)
    {
        EnsureValidName(name);

        Name = name;
        _errorHandler = errorHandler;
        _getSettings = getSettings;
    }
#else
    {
        EnsureValidName(name);

        Name = name;
        _errorHandler = errorHandler;
    }
#endif

    /// <summary>
    /// Gets the name that the token is stored under.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the folder that Windows App SDK apps keep the token's file in, or <see langword="null"/> for a folder named after the app's entry assembly in
    /// the user's local application data folder. Default is <see langword="null"/>. Other platforms don't use it.
    /// </summary>
    /// <exception cref="ArgumentException">In a Windows App SDK app, the value is not a fully qualified path.</exception>
    public string? FolderPath
    {
        get;
        init {
#if WINDOWS
            if (value is not null && !Path.IsPathFullyQualified(value))
                throw new ArgumentException($"The folder path '{value}' is not fully qualified.", nameof(value));
#endif
            field = value;
        }
    }

    /// <summary>
    /// Reads the stored token. Returns <see langword="null"/> if there is none, or if it can't be read, which is reported to the error handler.
    /// </summary>
    public string? Read()
    {
        try
        {
            return ReadCore();
        }
        catch (Exception ex)
        {
            _errorHandler?.Invoke(ex);
            return null;
        }
    }

    /// <summary>
    /// Stores the token, or removes the stored one if it is <see langword="null"/>. A failure is reported to the error handler. Matches the session token
    /// changed callback of an API client.
    /// </summary>
    /// <param name="token">The token to store, or <see langword="null"/> to remove the stored token.</param>
    public void Write(string? token)
    {
        try
        {
            WriteCore(token);
        }
        catch (Exception ex)
        {
            _errorHandler?.Invoke(ex);
        }
    }

    private static void EnsureValidName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (name.AsSpan().ContainsAny(InvalidNameChars))
            throw new ArgumentException($"The name '{name}' has a character that Windows doesn't allow in file names.", nameof(name));

        // Windows reserves a device name followed by an extension too, such as "NUL.dat", and ignores spaces before the extension.
        if (ReservedNames.Contains(name.Split('.')[0].TrimEnd(' ')))
            throw new ArgumentException($"The name '{name}' is a name that Windows reserves for a device.", nameof(name));
    }

#if __ANDROID__
    private string? ReadCore()
    {
        // Uno's vault reads as empty if it can't be read, such as when a backup restored it onto another device.
        if (FindCredential(new PasswordVault()) is not { } credential)
            return null;

        credential.RetrievePassword();
        return credential.Password;
    }

    private void WriteCore(string? token)
    {
        var vault = new PasswordVault();

        // Uno's vault replaces a credential with the same resource and user name.
        if (token is not null)
            vault.Add(new PasswordCredential(StoreName, Name, token));
        else if (FindCredential(vault) is { } credential)
            vault.Remove(credential);
    }

    // The vault compares resources and user names ignoring case.
    private PasswordCredential? FindCredential(PasswordVault vault) => vault.RetrieveAll().FirstOrDefault(c =>
        string.Equals(c.Resource, StoreName, StringComparison.OrdinalIgnoreCase) && string.Equals(c.UserName, Name, StringComparison.OrdinalIgnoreCase));
#elif __IOS__
    // Uno's PasswordVault isn't used on iOS: its keychain items have the default accessibility, which backups carry to other devices.

    private string? ReadCore()
    {
        var record = SecKeyChain.QueryAsRecord(CreateQuery(), out var status);

        if (status is SecStatusCode.ItemNotFound)
            return null;

        if (status is not SecStatusCode.Success)
            throw new InvalidOperationException($"The session token could not be read from the keychain ({status}).");

        return record?.ValueData?.ToString(NSStringEncoding.UTF8);
    }

    private void WriteCore(string? token)
    {
        if (token is null)
        {
            var removeStatus = SecKeyChain.Remove(CreateQuery());

            if (removeStatus is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
                throw new InvalidOperationException($"The session token could not be removed from the keychain ({removeStatus}).");

            return;
        }

        var data = NSData.FromString(token, NSStringEncoding.UTF8);

        // The item is changed in place, so that a write that fails (such as while the device is locked) keeps the token that was stored before.
        var status = SecKeyChain.Update(CreateQuery(), new SecRecord { ValueData = data });

        if (status is SecStatusCode.ItemNotFound)
        {
            status = SecKeyChain.Add(new SecRecord(SecKind.GenericPassword) {
                Service = StoreName,
                Account = Name,
                Accessible = SecAccessible.WhenUnlockedThisDeviceOnly,
                ValueData = data,
            });
        }

        if (status is not SecStatusCode.Success)
            throw new InvalidOperationException($"The session token could not be stored in the keychain ({status}).");
    }

    private SecRecord CreateQuery() => new(SecKind.GenericPassword) { Service = StoreName, Account = Name };
#elif __WASM__
    private string? ReadCore() => null;

    private void WriteCore(string? token)
    {
    }
#elif WINDOWS
    private string? ReadCore() => FileSessionTokenStorage.Read(GetFilePath());

    private void WriteCore(string? token) => FileSessionTokenStorage.Write(GetFilePath(), token);

    private string GetFilePath()
    {
        string folderPath = FolderPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Assembly.GetEntryAssembly()?.GetName().Name ?? AppDomain.CurrentDomain.FriendlyName);

        return Path.Combine(folderPath, Name + ".dat");
    }
#else
    private string? ReadCore() => SettingsSessionTokenStorage.Read(_getSettings(), Name);

    private void WriteCore(string? token) => SettingsSessionTokenStorage.Write(_getSettings(), Name, token);
#endif
}
