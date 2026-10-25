#if __WASM__

using System.Runtime.InteropServices.JavaScript;

namespace Singulink.Net.Http.Api.Client.Passkeys;

#pragma warning disable CA1416 // This call site is reachable on: 'BrowserWasm' 1.0 and later. The JS interop APIs are marked browser-only.

/// <summary>
/// Runs passkey ceremonies in the app's own page through the <c>passkeys.js</c> module.
/// </summary>
internal static partial class PasskeyInterop
{
    private const string ModuleName = "Singulink.Net.Http.Api.Client.Passkeys";

    // Uno deploys a library's content files to the app's package folder, under the library's assembly name (see the project file).
    private const string ModulePath = "Singulink.Net.Http.Api.Client.WinUI/passkeys.js";

    private static Task? _moduleLoadTask;

    /// <summary>
    /// Gets a value indicating whether the browser supports passkeys. Matches the module's <c>isSupported</c>, which can't be called before the module
    /// has loaded.
    /// </summary>
    public static bool IsSupported
    {
        get {
            if (JSHost.GlobalThis.GetTypeOfProperty("PublicKeyCredential") is not "function")
                return false;

            using var navigator = JSHost.GlobalThis.GetPropertyAsJSObject("navigator");
            return navigator?.GetTypeOfProperty("credentials") is "object";
        }
    }

    public static Task<string> GetCredentialAsync(string optionsJson, CancellationToken cancellationToken) =>
        RunAsync(GetCredentialCoreAsync, optionsJson, cancellationToken);

    public static Task<string> CreateCredentialAsync(string optionsJson, CancellationToken cancellationToken) =>
        RunAsync(CreateCredentialCoreAsync, optionsJson, cancellationToken);

    public static Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        return EnsureModuleLoadedAsync().WaitAsync(cancellationToken);
    }

    public static async Task SignalUnknownCredentialAsync(string credentialJson, string optionsJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!PasskeySignals.TryGetUnknownCredentialArguments(credentialJson, optionsJson, out string? rpId, out string? credentialId))
            return;

        try
        {
            await EnsureModuleLoadedAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
            await SignalUnknownCredentialCoreAsync(rpId, credentialId).WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is JSException or PasskeyAuthenticatorException)
        {
            // The signal is a hint, so a browser that refuses it or a script that didn't load changes nothing.
        }
    }

    // ConfigureAwait(true) in this class: JS interop calls must stay on the JS thread.
    private static async Task<string> RunAsync(Func<string, Task<string>> ceremony, string optionsJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureModuleLoadedAsync().ConfigureAwait(true);

        if (!IsSupportedCore())
            throw new PasskeyAuthenticatorException(PasskeyErrorReason.NotSupported, "This browser does not support passkeys.");

        var ceremonyTask = ceremony(optionsJson);

        try
        {
            // The browser's prompt can't be closed from here, so cancellation only stops waiting for it.
            return await ceremonyTask.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (JSException ex)
        {
            throw PasskeyPageProtocol.CreateScriptException(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ceremonyTask.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }

    private static async Task EnsureModuleLoadedAsync()
    {
        var loadTask = _moduleLoadTask ??= JSHost.ImportAsync(ModuleName, GetModuleUrl());

        try
        {
            await loadTask.ConfigureAwait(true);
        }
        catch (JSException ex)
        {
            // Lets a later call retry, e.g. after a network error, instead of failing for the rest of the session.
            _moduleLoadTask = null;
            throw new PasskeyAuthenticatorException(PasskeyErrorReason.Unknown, $"The passkeys script could not be loaded: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets the absolute address of the module. It has to be absolute because the runtime resolves relative module addresses against its own script
    /// rather than the page.
    /// </summary>
    private static string GetModuleUrl()
    {
        // The same composition Uno uses for app assets: the web app's base path, then the package folder.
        string packageFolder = Environment.GetEnvironmentVariable("UNO_BOOTSTRAP_APP_BASE") ?? string.Empty;
        string basePath = Environment.GetEnvironmentVariable("UNO_BOOTSTRAP_WEBAPP_BASE_PATH") ?? string.Empty;
        string path = packageFolder.Length > 0 ? $"{basePath}{packageFolder.TrimEnd('/')}/{ModulePath}" : ModulePath;

        using var document = JSHost.GlobalThis.GetPropertyAsJSObject("document");
        string? documentUri = document?.GetPropertyAsString("baseURI");

        return documentUri is null ? path : new Uri(new Uri(documentUri), path).AbsoluteUri;
    }

    [JSImport("isSupported", ModuleName)]
    [return: JSMarshalAs<JSType.Boolean>]
    private static partial bool IsSupportedCore();

    [JSImport("getCredential", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> GetCredentialCoreAsync([JSMarshalAs<JSType.String>] string optionsJson);

    [JSImport("createCredential", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> CreateCredentialCoreAsync([JSMarshalAs<JSType.String>] string optionsJson);

    [JSImport("signalUnknownCredential", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    private static partial Task<bool> SignalUnknownCredentialCoreAsync([JSMarshalAs<JSType.String>] string? rpId, [JSMarshalAs<JSType.String>] string credentialId);
}

#endif
