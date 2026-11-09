using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Validates <see cref="SessionHandlingOptions"/>, and that the allowed origins that session access is checked against are registered, so that a
/// misconfiguration fails at startup rather than when the first session is used. It checks options of every name.
/// </summary>
internal sealed class SessionHandlingOptionsValidator(IServiceProvider services) : IValidateOptions<SessionHandlingOptions>
{
    public ValidateOptionsResult Validate(string? name, SessionHandlingOptions options)
    {
        var failures = options.GetProblems();

        if (!IsRegistered(typeof(IOriginValidator)))
        {
            failures.Add(
                "Session handling requires the allowed origins, which are not registered. Register them with services.AddAllowedOrigins(...), which " +
                "requests that use a session are checked against.");
        }

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private bool IsRegistered(Type serviceType)
    {
        if (services.GetService<IServiceProviderIsService>() is { } isService)
            return isService.IsService(serviceType);

        return services.GetService(serviceType) is not null;
    }
}
