using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;

namespace Singulink.Net.Http.Api.Service.Passkeys;

[PrefixTestClass]
public sealed class PasskeyPageTests
{
    [TestMethod]
    public async Task AllowedScheme_ServesTheCeremony()
    {
        await using var host = await PageHost.StartAsync();

        var response = await host.Client.GetAsync(PageHost.PageUrl());
        string html = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        var data = PageHost.GetData(html);
        data["op"].ShouldBe("get");
        data["options"].ShouldBe(PageHost.RequestOptionsJson);
        data["redirect"].ShouldBe("myapp://passkey");
        data["state"].ShouldBe("s1");
        data["app-name"].ShouldBe("Example");
        data.ShouldNotContainKey("error-name");

        html.ShouldContain("Use your passkey");
        html.ShouldContain("""<button id="continue" class="primary" type="button">""");
        html.ShouldContain("""<button id="cancel" class="secondary" type="button">""");
    }

    [TestMethod]
    public async Task Create_ShowsTheUserName()
    {
        await using var host = await PageHost.StartAsync();

        string html = await host.Client.GetStringAsync(PageHost.PageUrl("create", PageHost.CreationOptionsJson));

        PageHost.GetData(html)["op"].ShouldBe("create");
        html.ShouldContain("Create a passkey");
        html.ShouldContain("Create a passkey for alex@example.com to sign in to Example");
    }

    [TestMethod]
    public async Task State_IsOptional()
    {
        await using var host = await PageHost.StartAsync();

        var response = await host.Client.GetAsync(PageHost.PageUrl(state: null));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        PageHost.GetData(await response.Content.ReadAsStringAsync()).ShouldNotContainKey("state");
    }

    [TestMethod]
    [DataRow("myapp://passkey")]
    [DataRow("MyApp://passkey")]
    [DataRow("myapp:/callback?x=1")]
    [DataRow("com.example.app://auth")]
    public async Task Schemes_MatchIgnoringCaseAndTrailingColon(string redirect)
    {
        await using var host = await PageHost.StartAsync(o => {
            o.AppName = "Example";
            o.RedirectSchemes.Add("MYAPP:");
            o.RedirectSchemes.Add("com.example.app");
        });

        var response = await host.Client.GetAsync(PageHost.PageUrl(redirect: redirect));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        PageHost.GetData(await response.Content.ReadAsStringAsync())["redirect"].ShouldBe(redirect);
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:5000/callback", "http://127.0.0.1:5000/callback")]
    [DataRow("http://127.0.0.1:49152/cb?x=1&y=2", "http://127.0.0.1:49152/cb?x=1&y=2")]
    [DataRow("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [DataRow("http://127.0.0.1/cb", "http://127.0.0.1/cb")]
    [DataRow("http://[::1]:49152/cb", "http://[::1]:49152/cb")]
    public async Task LoopbackRedirects_AreAllowed(string redirect, string expected)
    {
        await using var host = await PageHost.StartAsync();

        var response = await host.Client.GetAsync(PageHost.PageUrl(redirect: redirect));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        PageHost.GetData(await response.Content.ReadAsStringAsync())["redirect"].ShouldBe(expected);
    }

    [TestMethod]
    [DataRow("https://evil.example/cb")]
    [DataRow("http://evil.example/cb")]
    [DataRow("otherapp://passkey")]
    [DataRow("myappx://passkey")]
    [DataRow("javascript:alert(1)")]
    [DataRow("data:text/html,<script>alert(1)</script>")]
    [DataRow("intent://passkey#Intent;scheme=myapp;end")]
    [DataRow("http://localhost:5000/cb")]
    [DataRow("https://127.0.0.1:5000/cb")]
    [DataRow("http://127.0.0.2:5000/cb")]
    [DataRow("http://127.0.0.1.evil.example/cb")]
    [DataRow("http://user@127.0.0.1:5000/cb")]
    [DataRow("http://127.0.0.1:5000@evil.example/cb")]
    [DataRow("http://evil.example\\@127.0.0.1:5000/cb")]
    [DataRow("http://[::2]:5000/cb")]
    [DataRow(" myapp://passkey")]
    [DataRow("myapp://pass key")]
    [DataRow("myapp://passkey\n")]
    [DataRow("/relative/path")]
    [DataRow("")]
    public async Task OtherRedirects_AreRejected(string redirect)
    {
        await using var host = await PageHost.StartAsync();

        var response = await host.Client.GetAsync(PageHost.PageUrl(redirect: redirect));
        string html = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        AssertInvalid(html);
    }

    [TestMethod]
    public async Task MissingOrRepeatedRedirect_IsRejected()
    {
        await using var host = await PageHost.StartAsync();

        var missing = await host.Client.GetAsync(PageHost.PageUrl(redirect: null));
        var repeated = await host.Client.GetAsync(PageHost.PageUrl() + "&redirect=myapp%3A%2F%2Fother");

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        AssertInvalid(await missing.Content.ReadAsStringAsync());
        repeated.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        AssertInvalid(await repeated.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task LoopbackRedirects_CanBeDisabled()
    {
        await using var host = await PageHost.StartAsync(o => {
            o.AppName = "Example";
            o.RedirectSchemes.Add("myapp");
            o.AllowLoopbackRedirects = false;
        });

        var loopback = await host.Client.GetAsync(PageHost.PageUrl(redirect: "http://127.0.0.1:5000/cb"));
        var scheme = await host.Client.GetAsync(PageHost.PageUrl(redirect: "myapp://passkey"));

        loopback.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        AssertInvalid(await loopback.Content.ReadAsStringAsync());
        scheme.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task CustomSchemes_AreOnlyAllowedWhenConfigured()
    {
        await using var host = await PageHost.StartAsync(o => o.AppName = "Example");

        var scheme = await host.Client.GetAsync(PageHost.PageUrl(redirect: "myapp://passkey"));
        var loopback = await host.Client.GetAsync(PageHost.PageUrl(redirect: "http://127.0.0.1:5000/cb"));

        scheme.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        loopback.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task State_IsLimitedTo256Characters()
    {
        await using var host = await PageHost.StartAsync();

        var longest = await host.Client.GetAsync(PageHost.PageUrl(state: new string('s', 256)));
        var tooLong = await host.Client.GetAsync(PageHost.PageUrl(state: new string('s', 257)));
        var repeated = await host.Client.GetAsync(PageHost.PageUrl() + "&state=s2");

        longest.StatusCode.ShouldBe(HttpStatusCode.OK);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        AssertInvalid(await tooLong.Content.ReadAsStringAsync());
        repeated.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("sign")]
    [DataRow("GET")]
    public async Task UnknownOperation_IsReturnedToTheApp(string? op)
    {
        await using var host = await PageHost.StartAsync();

        var response = await host.Client.GetAsync(PageHost.PageUrl(op: op));
        var data = PageHost.GetData(await response.Content.ReadAsStringAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        data["redirect"].ShouldBe("myapp://passkey");
        data["state"].ShouldBe("s1");
        data["error-name"].ShouldBe("NotSupportedError");
        data.ShouldNotContainKey("op");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("not json")]
    [DataRow("[]")]
    [DataRow("""{"timeout":1000}""")]
    [DataRow("""{"challenge":5}""")]
    public async Task InvalidOptions_AreReturnedToTheApp(string? options)
    {
        await using var host = await PageHost.StartAsync();

        var response = await host.Client.GetAsync(PageHost.PageUrl(options: options));
        var data = PageHost.GetData(await response.Content.ReadAsStringAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        data["redirect"].ShouldBe("myapp://passkey");
        data["error-name"].ShouldBe("UnknownError");
        data.ShouldNotContainKey("options");
    }

    [TestMethod]
    [DataRow("!!!")]
    [DataRow("_w")]
    public async Task OptionsThatAreNotBase64UrlUtf8_AreReturnedToTheApp(string encodedOptions)
    {
        await using var host = await PageHost.StartAsync();

        var response = await host.Client.GetAsync($"/passkey?op=get&options={encodedOptions}&redirect=myapp%3A%2F%2Fpasskey");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        PageHost.GetData(await response.Content.ReadAsStringAsync())["error-name"].ShouldBe("UnknownError");
    }

    [TestMethod]
    public async Task Responses_HaveAStrictContentSecurityPolicyAndNoCaching()
    {
        await using var host = await PageHost.StartAsync();

        string[] urls = [
            PageHost.PageUrl(),
            PageHost.PageUrl(redirect: "https://evil.example/cb"),
            "/passkey/passkey-page.js",
            "/passkey/passkeys.js",
            "/passkey/passkey.css",
        ];

        foreach (string url in urls)
        {
            var response = await host.Client.GetAsync(url);

            string csp = response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
            csp.ShouldContain("default-src 'none'");
            csp.ShouldContain("script-src 'self'");
            csp.ShouldContain("style-src 'self'");
            csp.ShouldContain("base-uri 'none'");
            csp.ShouldContain("form-action 'none'");
            csp.ShouldContain("frame-ancestors 'none'");
            csp.ShouldNotContain("unsafe");
            csp.ShouldNotContain("connect-src");

            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
            response.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
            response.Headers.GetValues("Referrer-Policy").ShouldHaveSingleItem().ShouldBe("no-referrer");
            response.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
        }
    }

    [TestMethod]
    public async Task ScriptsAndStylesheet_AreServed()
    {
        await using var host = await PageHost.StartAsync();

        var pageScript = await host.Client.GetAsync("/passkey/passkey-page.js");
        var sharedScript = await host.Client.GetAsync("/passkey/passkeys.js");
        var style = await host.Client.GetAsync("/passkey/passkey.css");

        pageScript.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        (await pageScript.Content.ReadAsStringAsync()).ShouldContain("""import { isSupported, getCredential, createCredential } from "./passkeys.js";""");

        sharedScript.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        string shared = await sharedScript.Content.ReadAsStringAsync();
        shared.ShouldContain("export function isSupported()");
        shared.ShouldContain("export function getCredential(optionsJson)");
        shared.ShouldContain("export function createCredential(optionsJson)");

        style.Content.Headers.ContentType!.MediaType.ShouldBe("text/css");
    }

    [TestMethod]
    public async Task Scripts_MakeNoRequests()
    {
        await using var host = await PageHost.StartAsync();

        foreach (string url in (string[])["/passkey/passkey-page.js", "/passkey/passkeys.js"])
        {
            string script = await host.Client.GetStringAsync(url);

            foreach (string api in (string[])["fetch(", "XMLHttpRequest", "WebSocket", "sendBeacon", "EventSource", "import("])
                script.ShouldNotContain(api);
        }
    }

    [TestMethod]
    public async Task ResourceUrls_AreUnderThePagePath()
    {
        await using var host = await PageHost.StartAsync(pattern: "/auth/passkey");

        string html = await host.Client.GetStringAsync(PageHost.PageUrl(path: "/auth/passkey/"));

        html.ShouldContain("""<script type="module" src="/auth/passkey/passkey-page.js"></script>""");
        html.ShouldContain("""<link rel="stylesheet" href="/auth/passkey/passkey.css">""");
        (await host.Client.GetAsync("/auth/passkey/passkeys.js")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task AppName_IsEncoded()
    {
        await using var host = await PageHost.StartAsync(o => {
            o.AppName = "<b>Acme</b> & Co";
            o.RedirectSchemes.Add("myapp");
        });

        string html = await host.Client.GetStringAsync(PageHost.PageUrl());

        html.ShouldNotContain("<b>Acme</b>");
        html.ShouldContain("&lt;b&gt;Acme&lt;/b&gt; &amp; Co");
        PageHost.GetData(html)["app-name"].ShouldBe("<b>Acme</b> & Co");
    }

    [TestMethod]
    public async Task RequestValues_AreEncoded()
    {
        await using var host = await PageHost.StartAsync();
        const string State = "\"><script>alert(1)</script>";
        const string Options = """{"challenge":"\"><script>alert(2)</script>","rpId":"example.com"}""";

        string html = await (await host.Client.GetAsync(PageHost.PageUrl(options: Options, state: State))).Content.ReadAsStringAsync();

        html.ShouldNotContain("<script>alert");
        var data = PageHost.GetData(html);
        data["state"].ShouldBe(State);
        data["options"].ShouldBe(Options);
    }

    [TestMethod]
    public async Task AppName_DefaultsToTheRelyingPartyName()
    {
        await using var host = await PageHost.StartAsync(
            o => o.RedirectSchemes.Add("myapp"),
            configureServices: services => services.AddPasskeys<InMemoryPasskeyStore>(o => {
                o.RelyingPartyId = "example.com";
                o.RelyingPartyName = "Example RP";
                o.AllowedOrigins.Add("https://api.example.com");
            }));

        string html = await host.Client.GetStringAsync(PageHost.PageUrl());

        PageHost.GetData(html)["app-name"].ShouldBe("Example RP");
        html.ShouldContain("<title>Example RP</title>");
    }

    [TestMethod]
    public async Task AppName_IsRequiredWithoutPasskeyServices()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => PageHost.StartAsync(o => o.RedirectSchemes.Add("myapp")));
    }

    [TestMethod]
    [DataRow("https")]
    [DataRow("HTTP")]
    [DataRow("javascript:")]
    [DataRow("intent")]
    [DataRow("data")]
    [DataRow("my app")]
    [DataRow("1app")]
    [DataRow("")]
    public async Task ReservedOrInvalidSchemes_AreNotAllowed(string scheme)
    {
        await Should.ThrowAsync<ArgumentException>(() => PageHost.StartAsync(o => {
            o.AppName = "Example";
            o.RedirectSchemes.Add(scheme);
        }));
    }

    [TestMethod]
    public async Task SomeRedirect_MustBeAllowed()
    {
        await Should.ThrowAsync<ArgumentException>(() => PageHost.StartAsync(o => {
            o.AppName = "Example";
            o.AllowLoopbackRedirects = false;
        }));
    }

    [TestMethod]
    public async Task Page_IsExcludedFromApiDescriptions()
    {
        await using var host = await PageHost.StartAsync();

        var endpoints = host.Endpoints.OfType<RouteEndpoint>().ToList();

        endpoints.Select(e => e.RoutePattern.RawText).ShouldBe(
            ["/passkey/", "/passkey/passkey-page.js", "/passkey/passkeys.js", "/passkey/passkey.css"],
            ignoreOrder: true);

        foreach (var endpoint in endpoints)
            endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!.ExcludeFromDescription.ShouldBeTrue();
    }

    private static void AssertInvalid(string html)
    {
        var data = PageHost.GetData(html);

        data.ShouldNotContainKey("redirect");
        data.ShouldNotContainKey("op");
        data.ShouldNotContainKey("options");
        html.ShouldContain("This link is not valid");
        html.ShouldContain("""<button id="continue" class="primary" type="button" hidden>""");
        html.ShouldContain("""<button id="cancel" class="secondary" type="button" hidden>""");
    }
}
