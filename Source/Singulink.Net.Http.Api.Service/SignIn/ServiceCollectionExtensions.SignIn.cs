using Microsoft.Extensions.DependencyInjection.Extensions;
using Singulink.Net.Http.Api.Service.SignIn;

namespace Singulink.Net.Http.Api.Service;

/// <content>
/// Registration of the sign-in building blocks.
/// </content>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SignInCodes"/> for one-time sign-in codes bound to PKCE code challenges, and the <see cref="IShortLivedStore"/> that keeps them
    /// (see <see cref="AddShortLivedStore"/>). Services that are already registered are kept, so it can be called more than once.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure options.</param>
    public static IServiceCollection AddSignInCodes(this IServiceCollection services, Action<SignInCodeOptions>? configure = null)
    {
        services.AddShortLivedStore();
        services.AddOptions<SignInCodeOptions>();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<SignInCodes>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="DeviceLinks"/> for signing in new devices with the approval of a signed-in device, and the sign-in codes it issues (see <see
    /// cref="AddSignInCodes(IServiceCollection, Action{SignInCodeOptions}?)"/>).
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
