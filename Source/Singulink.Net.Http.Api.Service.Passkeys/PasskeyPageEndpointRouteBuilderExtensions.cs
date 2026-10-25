using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Passkeys;
using Singulink.Net.Http.Api.Service.Passkeys.Internal;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for mapping the passkey hand-off page in an <see cref="IEndpointRouteBuilder"/>.
/// </summary>
public static class PasskeyPageEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the passkey hand-off page, which runs a WebAuthn ceremony in the system browser for apps that cannot run it themselves (such as native apps)
    /// and returns the credential to the app's callback address.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The route of the page. Its script and stylesheet are served under it.</param>
    /// <param name="configure">An action that configures the page options, such as the custom schemes allowed in callback addresses.</param>
    /// <returns>A builder for adding conventions to the page's endpoints.</returns>
    /// <remarks>
    /// <para>
    /// The page is opened with <c>{pattern}?op={get|create}&amp;options={options}&amp;redirect={callback}&amp;state={state}</c>, where <c>options</c> is
    /// the base64url (without padding) encoding of the UTF-8 options JSON of a ceremony (<see cref="PasskeyCeremony.OptionsJson"/>), <c>redirect</c> is
    /// the app's callback address and <c>state</c> is an optional value of at most 256 characters. When the user continues, the page runs the ceremony and
    /// navigates to the callback address with <c>state</c> and either <c>credential</c> (the base64url encoding of the UTF-8 credential JSON) or
    /// <c>error</c> (the name of the error, such as <c>NotAllowedError</c>, or <c>AbortError</c> when the user cancels) and <c>message</c> added. The
    /// page makes no requests of its own: the app gets the options from its API and posts the credential back to it.
    /// </para>
    /// <para>
    /// The page must be served from an origin that is in <see cref="PasskeyOptions.AllowedOrigins"/> and whose host is the relying party ID or a
    /// subdomain of it, for example an API at <c>https://api.example.com</c> for the relying party ID <c>example.com</c>. It is excluded from API
    /// descriptions, and is sent with a strict content security policy and no caching.
    /// </para>
    /// </remarks>
    public static IEndpointConventionBuilder MapPasskeyPage(
        this IEndpointRouteBuilder endpoints, string pattern = "/passkey", Action<PasskeyPageOptions>? configure = null)
    {
        var options = new PasskeyPageOptions();
        configure?.Invoke(options);

        string? appName = options.AppName;

        if (string.IsNullOrWhiteSpace(appName))
            appName = endpoints.ServiceProvider.GetService<IOptions<PasskeyOptions>>()?.Value.RelyingPartyName;

        if (string.IsNullOrWhiteSpace(appName))
            throw new InvalidOperationException($"Set {nameof(PasskeyPageOptions)}.{nameof(PasskeyPageOptions.AppName)} for the passkey page.");

        var page = new PasskeyPage(options, appName, new PageTemplate(ReadResource("passkey.html")));

        byte[] script = Encoding.UTF8.GetBytes(ReadResource(PasskeyPage.ScriptFileName));
        byte[] sharedScript = Encoding.UTF8.GetBytes(ReadResource(PasskeyPage.SharedScriptFileName));
        byte[] style = Encoding.UTF8.GetBytes(ReadResource(PasskeyPage.StyleFileName));

        const string JavaScript = "text/javascript; charset=utf-8";

        var group = endpoints.MapGroup(pattern);
        group.ExcludeFromDescription();

        group.MapGet(string.Empty, (RequestDelegate)page.WriteAsync);
        group.MapGet($"/{PasskeyPage.ScriptFileName}", (RequestDelegate)(context => PasskeyPage.WriteFileAsync(context, JavaScript, script)));
        group.MapGet($"/{PasskeyPage.SharedScriptFileName}", (RequestDelegate)(context => PasskeyPage.WriteFileAsync(context, JavaScript, sharedScript)));
        group.MapGet($"/{PasskeyPage.StyleFileName}", (RequestDelegate)(context => PasskeyPage.WriteFileAsync(context, "text/css; charset=utf-8", style)));

        return group;
    }

    private static string ReadResource(string fileName)
    {
        using var stream = typeof(PasskeyPageEndpointRouteBuilderExtensions).Assembly.GetManifestResourceStream($"Passkeys.Page.{fileName}") ??
            throw new InvalidOperationException($"Missing embedded resource for '{fileName}'.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
