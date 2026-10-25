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
    /// <see cref="ChunkedUploadEndpointRouteBuilderExtensions"/>), including a hosted service that removes expired uploads. Uploads belong to the
    /// signed-in user of the session token type, and are kept in the directory store unless the app registers an <see cref="IUploadStore"/> of its own
    /// as a singleton, before or after this call.
    /// </summary>
    /// <typeparam name="TSessionToken">The session token type of the app's session handling.</typeparam>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure options, which are validated when the app starts.</param>
    /// <remarks>
    /// The application fails to start if session handling is not registered for <typeparamref name="TSessionToken"/>, or if
    /// <see cref="ChunkedUploadOptions.Directory"/> is not set and the app registers no <see cref="IUploadStore"/>.
    /// </remarks>
    public static IServiceCollection AddChunkedUploads<TSessionToken>(this IServiceCollection services, Action<ChunkedUploadOptions>? configure = null)
        where TSessionToken : class, ISessionToken
    {
        services.AddLogging();
        services.AddOptions<ChunkedUploadOptions>().ValidateOnStart();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ChunkedUploadOptions>, ChunkedUploadOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IUploadStore, DirectoryUploadStore>();
        services.TryAddSingleton<UploadUser, UploadUser<TSessionToken>>();
        services.TryAddSingleton<ChunkedUploads>();
        services.AddHostedService<ChunkedUploadCleanup>();

        return services;
    }
}
