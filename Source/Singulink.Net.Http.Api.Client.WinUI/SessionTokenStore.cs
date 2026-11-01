using System.Buffers;
#if WINDOWS
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
/// another device or Windows user, reads as <see langword="null"/>. It is removed then, except on Android, where it stays until the next write.
/// </para>
/// <para>
/// A write that fails keeps the token that was stored before: on iOS the stored item is changed in place, and elsewhere the stored token is replaced as a
/// whole. On Android before version 6.0, the password vault can't be used, so reads and writes fail and no token is stored.
/// </para>
/// <para>
/// On iOS and Android, a token that an earlier version kept in the app's local settings under <see cref="Name"/>, unencrypted, is moved into the keychain
/// or the password vault, and removed from the settings, when they have no token.
/// </para>
/// </remarks>
public sealed class SessionTokenStore
{
    private const string DefaultName = "SessionToken";

    // The characters that Windows doesn't allow in file names, on every platform (Path.GetInvalidFileNameChars depends on the platform).
    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create([.. Enumerable.Range(0, 32).Select(c => (char)c), .. "\"*/:<>?\\|"]);

    // The names that Windows reserves for devices, also with an extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private readonly Action<Exception>? _errorHandler;

#if !__WASM__ && !WINDOWS
    private readonly Func<IDictionary<string, object>> _getSettings;

    // The iOS and Android storage, or null on other platforms.
    private readonly ISessionTokenStorage? _platformStorage;
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
#if !__WASM__ && !WINDOWS
        : this(name, errorHandler, () => ApplicationData.Current.LocalSettings.Values, PlatformSessionTokenStorage.Create(name))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionTokenStore"/> class that keeps the token in the specified iOS or Android storage, or in the
    /// specified settings if there is none.
    /// </summary>
    /// <param name="name">The name that the token is stored under.</param>
    /// <param name="errorHandler">A callback that receives the exceptions of reads and writes that failed.</param>
    /// <param name="getSettings">Gets the app's local settings.</param>
    /// <param name="platformStorage">The iOS or Android storage, which the token stored in the settings is moved to, or <see langword="null"/> to keep the
    /// token in the settings.</param>
    internal SessionTokenStore(string name, Action<Exception>? errorHandler, Func<IDictionary<string, object>> getSettings,
        ISessionTokenStorage? platformStorage = null)
    {
        EnsureValidName(name);

        Name = name;
        _errorHandler = errorHandler;
        _getSettings = getSettings;
        _platformStorage = platformStorage;
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

#if __WASM__
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
    private string? ReadCore()
    {
        if (_platformStorage is null)
            return SettingsSessionTokenStorage.Read(_getSettings(), Name);

        if (_platformStorage.Read() is { } token)
            return token;

        // Versions that kept the token in the app's settings on iOS and Android, such as Uno Platform apps with the Skia renderer on version 4.0.0 of this
        // library, stored it unencrypted, so it is moved over and removed from the settings.
        var settings = _getSettings();

        if (SettingsSessionTokenStorage.Read(settings, Name) is not { } settingsToken)
            return null;

        try
        {
            _platformStorage.Write(settingsToken);
            settings.Remove(Name);
        }
        catch (Exception ex)
        {
            // The token stays in the settings, so that the next read moves it, and the session goes on with it.
            _errorHandler?.Invoke(ex);
        }

        return settingsToken;
    }

    private void WriteCore(string? token)
    {
        if (_platformStorage is null)
        {
            SettingsSessionTokenStorage.Write(_getSettings(), Name, token);
            return;
        }

        // A token that a failed move left in the settings is out of date once there is another token or none, and would otherwise be moved over once the
        // session ends. When the token is removed, both go even if the other fails. After a new token, it goes once the token is stored, so that a write
        // that fails keeps it.
        if (token is null)
        {
            try
            {
                _getSettings().Remove(Name);
            }
            finally
            {
                _platformStorage.Write(null);
            }

            return;
        }

        _platformStorage.Write(token);
        _getSettings().Remove(Name);
    }
#endif
}
