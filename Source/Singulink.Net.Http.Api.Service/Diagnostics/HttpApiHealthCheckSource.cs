using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// A feature of the toolkit that a registration method registered, with the checks that
/// <see cref="HttpApiHealthChecksBuilderExtensions.AddHttpApiChecks"/> adds for it. It is registered as an instance, and is never resolved.
/// </summary>
/// <param name="Feature">The name of the feature, which is registered once.</param>
/// <param name="Add">Adds the feature's checks.</param>
internal sealed record HttpApiHealthCheckSource(string Feature, Action<IHealthChecksBuilder> Add)
{
    /// <summary>
    /// Registers the checks of a feature, unless they are registered already.
    /// </summary>
    public static void Register(IServiceCollection services, string feature, Action<IHealthChecksBuilder> add)
    {
        if (!services.Any(d => d.ImplementationInstance is HttpApiHealthCheckSource source && source.Feature == feature))
            services.AddSingleton(new HttpApiHealthCheckSource(feature, add));
    }
}
