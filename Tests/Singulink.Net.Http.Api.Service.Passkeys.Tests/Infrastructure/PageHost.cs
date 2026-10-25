using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;

/// <summary>
/// An in-process web application (using the test server) serving the passkey page through the toolkit's API response handling.
/// </summary>
public sealed partial class PageHost : IAsyncDisposable
{
    public const string RequestOptionsJson =
        """{"challenge":"q83vEjRWeJCrze8SNFZ4kA","timeout":180000,"rpId":"example.com","allowCredentials":[],"userVerification":"required","hints":[]}""";

    public const string CreationOptionsJson = """
        {
          "rp": { "id": "example.com", "name": "Example" },
          "user": { "name": "alex@example.com", "id": "dXNlci0x", "displayName": "Alex Smith" },
          "challenge": "q83vEjRWeJCrze8SNFZ4kA",
          "pubKeyCredParams": [{ "type": "public-key", "alg": -7 }],
          "timeout": 180000,
          "attestation": "none"
        }
        """;

    private PageHost(WebApplication app)
    {
        App = app;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public IReadOnlyList<Endpoint> Endpoints => ((IEndpointRouteBuilder)App).DataSources.SelectMany(s => s.Endpoints).ToList();

    public static async Task<PageHost> StartAsync(
        Action<PasskeyPageOptions>? configure = null,
        string pattern = "/passkey",
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddApiResponseHandling();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapPasskeyPage(pattern, configure ?? ConfigureDefault);
        app.UseApiResponseHandling();

        await app.StartAsync();
        return new PageHost(app);
    }

    public static void ConfigureDefault(PasskeyPageOptions options)
    {
        options.AppName = "Example";
        options.RedirectSchemes.Add("myapp");
    }

    public static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    public static string PageUrl(string? op = "get", string? options = RequestOptionsJson, string? redirect = "myapp://passkey", string? state = "s1", string path = "/passkey")
    {
        List<string> query = [];

        if (op is not null)
            query.Add($"op={Uri.EscapeDataString(op)}");

        if (options is not null)
            query.Add($"options={Encode(options)}");

        if (redirect is not null)
            query.Add($"redirect={Uri.EscapeDataString(redirect)}");

        if (state is not null)
            query.Add($"state={Uri.EscapeDataString(state)}");

        return $"{path}?{string.Join('&', query)}";
    }

    /// <summary>
    /// Gets the decoded data attributes of the page's <c>main</c> element.
    /// </summary>
    public static Dictionary<string, string> GetData(string html)
    {
        var main = MainRegex().Match(html);
        main.Success.ShouldBeTrue("The page has no main element.");

        return DataRegex().Matches(main.Groups[1].Value).ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }

    [GeneratedRegex(@"<main\s([^>]*)>")]
    private static partial Regex MainRegex();

    [GeneratedRegex(@"data-([a-z-]+)=""([^""]*)""")]
    private static partial Regex DataRegex();
}
