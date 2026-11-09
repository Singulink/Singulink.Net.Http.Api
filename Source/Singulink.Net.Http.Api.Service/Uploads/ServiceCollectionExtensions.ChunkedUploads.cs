using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <content>
/// Registration of chunked uploads.
/// </content>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services of chunked uploads, whose endpoints are mapped with <c>MapChunkedUpload</c> (see
    /// <see cref="ChunkedUploadEndpointRouteBuilderExtensions"/>), including a hosted service that removes expired uploads.
    /// </summary>
    /// <typeparam name="TSessionToken">The session token type of the app's session handling.</typeparam>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure options, which are validated when the application starts.</param>
    /// <remarks>
    /// <para>
    /// Uploads belong to the signed-in user of the session token type, and are kept in the <see cref="IUploadStore"/> that the app registers as a
    /// singleton, before or after this call: the directory store (see <see cref="AddDirectoryUploadStore"/>) or a store of its own. The limits of a user's
    /// open uploads are the options' unless the app registers an <see cref="IUploadLimitsProvider"/>, also before or after this call. The open uploads are
    /// counted in the <see cref="IShortLivedStore"/> of <see cref="ShortLivedStoreKeys.Uploads"/>, which this registers unless one is registered (see
    /// <see cref="AddShortLivedStore"/>).
    /// </para>
    /// <para>
    /// The application fails to start if session handling is not registered for <typeparamref name="TSessionToken"/>, or if the app registers no
    /// <see cref="IUploadStore"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddChunkedUploads<TSessionToken>(this IServiceCollection services, Action<ChunkedUploadOptions>? configure = null)
        where TSessionToken : class, ISessionToken
    {
        services.AddLogging();
        services.AddMetrics();
        services.AddOptions<ChunkedUploadOptions>().ValidateOnStart();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ChunkedUploadOptions>, ChunkedUploadOptionsValidator>());
        services.AddShortLivedStore();
        services.TryAddSingleton<UploadUser, UploadUser<TSessionToken>>();
        services.TryAddSingleton<ChunkedUploads>();
        services.AddHostedService<ChunkedUploadCleanup>();

        HttpApiHealthCheckSource.Register(services, "uploads", b => b.AddChunkedUploadsCheck().AddFeatureStoreCheck(ShortLivedStoreKeys.Uploads));

        return services;
    }

    /// <summary>
    /// Registers the directory store as the <see cref="IUploadStore"/> of chunked uploads (see
    /// <see cref="AddChunkedUploads{TSessionToken}(IServiceCollection, Action{ChunkedUploadOptions}?)"/>), which keeps uploads in a directory on a local
    /// disk or a share, as the uploads guide describes.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure options, which are validated when the application starts.
    /// <see cref="DirectoryUploadStoreOptions.Directory"/> and <see cref="DirectoryUploadStoreOptions.SharingMode"/> must be set, here or through
    /// configuration.</param>
    /// <remarks>
    /// <para>
    /// The store is a singleton created from the <see cref="DirectoryUploadStoreOptions"/> of the options system.
    /// </para>
    /// <para>
    /// The application fails to start if the options are not valid, such as when a required option is not set (the <see cref="OptionsValidationException"/>
    /// lists every problem), or, with <see cref="StoreSharingMode.ProcessLocal"/>, if another process holds the directory's lock (see
    /// <see cref="DirectoryUploadStoreOptions.SharingMode"/>).
    /// </para>
    /// <para>
    /// An <see cref="IUploadStore"/> that is already registered is kept, so calling this again only adds <paramref name="configure"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDirectoryUploadStore(this IServiceCollection services, Action<DirectoryUploadStoreOptions>? configure = null)
    {
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<DirectoryUploadStoreOptions>().ValidateOnStart();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DirectoryUploadStoreOptions>, DirectoryUploadStoreOptionsValidator>());

        services.TryAddSingleton<IUploadStore>(provider => new DirectoryUploadStore(
            provider.GetRequiredService<IOptions<DirectoryUploadStoreOptions>>().Value,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>()));

        return services;
    }
}
