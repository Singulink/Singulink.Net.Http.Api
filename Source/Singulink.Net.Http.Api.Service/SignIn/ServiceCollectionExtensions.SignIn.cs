using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Singulink.Net.Http.Api.Service.SignIn;

namespace Singulink.Net.Http.Api.Service;

/// <content>
/// Registration of the sign-in building blocks.
/// </content>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SignInCodes"/> for one-time sign-in codes bound to PKCE code challenges, and the <see cref="IShortLivedStore"/> that keeps them,
    /// which they resolve with <see cref="ShortLivedStoreKeys.SignIn"/> (see <see cref="AddShortLivedStore"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A warning is logged when the application starts if that store keeps its values in this process while another store is shared with other processes.
    /// Services that are already registered are kept, so it can be called more than once.
    /// </para>
    /// <para>
    /// The check that warns resolves the other stores when the application starts: the stores of the other keys, the store registered without a key, and the
    /// <see cref="Uploads.IUploadStore"/>, if one is registered. An upload store that fails to be created, such as the directory store (see
    /// <see cref="AddDirectoryUploadStore"/>) whose directory another process has locked, then fails the application's start from that check.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure options.</param>
    public static IServiceCollection AddSignInCodes(this IServiceCollection services, Action<SignInCodeOptions>? configure = null)
    {
        services.AddShortLivedStore();
        services.AddOptions<SignInCodeOptions>();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<SignInCodes>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SignInStoreCheck>());
        HttpApiHealthCheckSource.Register(services, "sign-in", b => b.AddFeatureStoreCheck(ShortLivedStoreKeys.SignIn));

        return services;
    }

    /// <summary>
    /// Registers <see cref="DeviceLinks"/> for signing in new devices with the approval of a signed-in device, and the sign-in codes it issues (see
    /// <see cref="AddSignInCodes(IServiceCollection, Action{SignInCodeOptions}?)"/>).
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure options.</param>
    public static IServiceCollection AddDeviceLinks(this IServiceCollection services, Action<DeviceLinkOptions>? configure = null)
    {
        services.AddSignInCodes();
        services.AddOptions<DeviceLinkOptions>();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<DeviceLinks>();

        return services;
    }
}
