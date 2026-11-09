namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Provides the service keys of the <see cref="IShortLivedStore"/> of each feature of the toolkit, which resolves its store as a keyed service with its
/// key.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServiceCollectionExtensions.AddShortLivedStore"/> registers a store for every key (<see cref="KeyedService.AnyKey"/>): while the app's
/// <see cref="IShortLivedStore"/> is the default <see cref="MemoryShortLivedStore"/>, each key gets a memory store of its own, so that one feature can't fill
/// the memory of another, configured with the named options of its key, such as
/// <c>services.Configure&lt;MemoryShortLivedStoreOptions&gt;(ShortLivedStoreKeys.Uploads, o =&gt; o.MaxSize = ...)</c>. Once the app registers an
/// <see cref="IShortLivedStore"/> of its own, every key gets that store.
/// </para>
/// <para>
/// A store registered for a key, such as with
/// <c>services.AddKeyedSingleton&lt;IShortLivedStore&gt;(ShortLivedStoreKeys.Uploads, ...)</c>, takes the place of that key's store, and one registered for
/// <see cref="KeyedService.AnyKey"/> the place of every key's. The app's own values keep using the <see cref="IShortLivedStore"/> registered without a key.
/// </para>
/// </remarks>
public static class ShortLivedStoreKeys
{
    /// <summary>
    /// The key of the store of sign-in codes (see <see cref="SignIn.SignInCodes"/>), device links (see <see cref="SignIn.DeviceLinks"/>) and passkey
    /// ceremonies.
    /// </summary>
    /// <remarks>
    /// All the servers of a service must share it. A warning is logged when the application starts if it keeps its values in this process while another
    /// store, or the upload store, is shared with other processes.
    /// </remarks>
    public const string SignIn = "httpapi:sign-in";

    /// <summary>
    /// The key of the store of the counts of client rate limits.
    /// </summary>
    /// <remarks>
    /// A store that keeps its values in this process limits each server on its own.
    /// </remarks>
    public const string RateLimiting = "httpapi:rate-limiting";

    /// <summary>
    /// The key of the store of chunked uploads, which keeps the counts of open uploads and the cancellations of uploads.
    /// </summary>
    /// <remarks>
    /// It must be shared when the upload store is (see <see cref="Uploads.IUploadStore.SharingMode"/>): the application fails to start otherwise.
    /// </remarks>
    public const string Uploads = "httpapi:uploads";

    /// <summary>
    /// Gets a value indicating whether a key is one of the toolkit's keys.
    /// </summary>
    internal static bool IsToolkitKey(string? key) => key is SignIn or RateLimiting or Uploads;
}
