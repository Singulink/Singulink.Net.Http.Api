using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Service.Passkeys;
using Singulink.Net.Http.Api.Service.SignIn;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for registering passkey services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class PasskeyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PasskeyService"/> (scoped) with the app's passkey store, the <see cref="SignInCodes"/> that a passkey sign-in can continue with,
    /// and the <see cref="IShortLivedStore"/> that keeps ceremonies between their begin and finish requests (see <see
    /// cref="ServiceCollectionExtensions.AddSignInCodes(IServiceCollection, Action{SignInCodeOptions}?)"/>).
    /// </summary>
    /// <typeparam name="TStore">The app's passkey store, which is registered as a scoped <see cref="IPasskeyStore"/>.</typeparam>
    /// <param name="services">The service collection to add the passkey services to.</param>
    /// <param name="configure">An action that configures the options. Options can also be bound from configuration, for example with
    /// <c>services.AddPasskeys&lt;TStore&gt;(o =&gt; builder.Configuration.GetSection("Passkeys").Bind(o))</c>.</param>
    /// <remarks>
    /// The options are validated when the app starts.
    /// </remarks>
    public static IServiceCollection AddPasskeys<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this IServiceCollection services, Action<PasskeyOptions>? configure = null)
        where TStore : class, IPasskeyStore
    {
        var optionsBuilder = services.AddOptions<PasskeyOptions>().ValidateOnStart();

        if (configure is not null)
            optionsBuilder.Configure(configure);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PasskeyOptions>, PasskeyOptionsValidator>());
        services.AddSignInCodes();
        services.AddScoped<IPasskeyStore, TStore>();

        services.TryAddScoped(sp => new PasskeyService(
            sp.GetRequiredService<IOptions<PasskeyOptions>>().Value,
            sp.GetRequiredService<IShortLivedStore>(),
            sp.GetRequiredService<IPasskeyStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PasskeyService>>()));

        return services;
    }
}
