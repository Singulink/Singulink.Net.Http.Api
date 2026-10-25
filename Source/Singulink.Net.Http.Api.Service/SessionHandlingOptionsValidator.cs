using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Validates <see cref="SessionHandlingOptions"/>, and that the allowed origins that session access is checked against are registered, so that a
/// misconfiguration fails at startup rather than when the first session is used.
/// </summary>
internal sealed class SessionHandlingOptionsValidator(IServiceProvider services) : IValidateOptions<SessionHandlingOptions>
{
    private const string HostCookiePrefix = "__Host-";

    public ValidateOptionsResult Validate(string? name, SessionHandlingOptions options)
    {
        List<string> failures = [];

        // A session is only refreshed when its cookie is due, after RefreshAfter, so an expiry that is not longer than that ends sessions that are in use.

        CheckExpiry(options.TempSessionExpiry, nameof(SessionHandlingOptions.TempSessionExpiry));
        CheckExpiry(options.PersistentSessionExpiry, nameof(SessionHandlingOptions.PersistentSessionExpiry));

        // Browsers reject a __Host- cookie that has a domain or a path other than "/", which would leave every client without a session.
        if (options.SessionCookieName.StartsWith(HostCookiePrefix, StringComparison.OrdinalIgnoreCase) &&
            (options.CookieDomain is not null || options.CookiePath is not "/"))
        {
            failures.Add(
                $"{nameof(SessionHandlingOptions)}.{nameof(SessionHandlingOptions.SessionCookieName)} '{options.SessionCookieName}' starts with " +
                $"'{HostCookiePrefix}', which browsers only accept for a cookie without a domain and with the path '/', but " +
                (options.CookieDomain is not null ?
                    $"{nameof(SessionHandlingOptions.CookieDomain)} is '{options.CookieDomain}'. " :
                    $"{nameof(SessionHandlingOptions.CookiePath)} is '{options.CookiePath}'. ") +
                "Choose a cookie name without the prefix (and the same name in the clients).");
        }

        if (!IsRegistered(typeof(IOriginValidator)))
        {
            failures.Add(
                "Session handling requires the allowed origins, which are not registered. Register them with services.AddAllowedOrigins(...), which " +
                "requests that use a session are checked against.");
        }

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

        void CheckExpiry(TimeSpan expiry, string propertyName)
        {
            if (expiry <= options.RefreshAfter)
            {
                failures.Add(
                    $"{nameof(SessionHandlingOptions)}.{propertyName} ({expiry}) must be longer than {nameof(SessionHandlingOptions)}." +
                    $"{nameof(SessionHandlingOptions.RefreshAfter)} ({options.RefreshAfter}), otherwise sessions end before they are refreshed.");
            }
        }
    }

    private bool IsRegistered(Type serviceType)
    {
        if (services.GetService<IServiceProviderIsService>() is { } isService)
            return isService.IsService(serviceType);

        return services.GetService(serviceType) is not null;
    }
}
