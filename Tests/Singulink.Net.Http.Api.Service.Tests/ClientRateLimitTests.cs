using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Rate limit tests, run with requests counted in memory and in a shared store. Every host of a test runs on the same clock, which only moves when the test
/// advances it, so a window never ends in the middle of a test.
/// </summary>
public abstract class ClientRateLimitTests
{
    protected const string Policy = "test-policy";

    // Sets the connection's remote address for a request. "none" sets no address (as for a Unix domain socket connection).
    private const string ClientIpHeader = "X-Test-Client-IP";

    protected const string ClientA = "203.0.113.10";
    protected const string ClientB = "198.51.100.20";

    protected abstract StoreKind StoreKind { get; }

    protected TestClock Clock { get; } = new();

    /// <summary>
    /// Gets the store that all the hosts of a test share when requests are counted in a shared store.
    /// </summary>
    protected SharedShortLivedStore SharedStore => field ??= new SharedShortLivedStore(Clock);

    protected Task<TestWebHost> StartAsync(Action<RateLimiterOptions> configure, bool requirePolicy = true, IShortLivedStore? store = null)
    {
        return TestWebHost.StartAsync(
            app => {
                app.Use((context, next) => {
                    string ip = context.Request.Headers[ClientIpHeader].ToString();
                    context.Connection.RemoteIpAddress = ip is "none" ? null : IPAddress.Parse(ip);

                    return next(context);
                });

                app.UseRateLimiter();

                var limited = app.MapGet("/limited", () => "ok");
                app.MapGet("/other", () => "ok");

                if (requirePolicy)
                    limited.RequireRateLimiting(Policy);
            },
            configureServices: services => {
                services.AddRateLimiter(configure);
                services.AddSingleton<TimeProvider>(Clock);

                // With the memory store, no store is registered: requests are then counted in memory, as they are in services without sign-in features.
                if (store is not null)
                    services.AddSingleton(store);
                else if (StoreKind is StoreKind.Shared)
                    services.AddSingleton<IShortLivedStore>(SharedStore);
            });
    }

    protected Task<TestWebHost> StartWithPolicyAsync(Action<ClientRateLimitOptions> configure)
    {
        return StartAsync(o => o.AddClientFixedWindowLimiter(Policy, configure).SetApiRejectionResponse());
    }

    protected static Task<HttpResponseMessage> SendAsync(
        TestWebHost host, string clientIp, string path = "/limited", params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(ClientIpHeader, clientIp);

        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);

        return host.Client.SendAsync(request);
    }

    protected static async Task<HttpStatusCode[]> SendManyAsync(TestWebHost host, string clientIp, int count, string path = "/limited")
    {
        var statuses = new HttpStatusCode[count];

        for (int i = 0; i < count; i++)
            statuses[i] = (await SendAsync(host, clientIp, path)).StatusCode;

        return statuses;
    }

    [TestMethod]
    public async Task Limit_AppliesToEachClientAddressSeparately()
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 2);

        (await SendManyAsync(host, ClientA, 3)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await SendManyAsync(host, ClientB, 3)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);

        // Endpoints without the policy are not limited.
        (await SendAsync(host, ClientA, "/other")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task Ipv4MappedAddress_SharesLimitWithIpv4Address()
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 1);

        (await SendAsync(host, ClientA)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(host, "::ffff:" + ClientA)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [TestMethod]
    public async Task Ipv6Clients_AreLimitedPerSlash64()
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 1);

        (await SendAsync(host, "2001:db8:1:2::1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(host, "2001:db8:1:2:ffff::2")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await SendAsync(host, "2001:db8:1:3::1")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow("10.1.2.3")]
    [DataRow("172.16.0.1")]
    [DataRow("172.31.255.254")]
    [DataRow("192.168.1.1")]
    [DataRow("127.0.0.1")]
    [DataRow("169.254.10.20")]
    [DataRow("100.64.0.1")]
    [DataRow("100.127.255.254")]
    [DataRow("::1")]
    [DataRow("fe80::1")]
    [DataRow("fd12:3456:789a::1")]
    [DataRow("fc00::1")]
    [DataRow("::ffff:10.1.2.3")]
    [DataRow("::ffff:127.0.0.1")]
    [DataRow("none")]
    public async Task InternalClients_AreExempt_WhenPrivateNetworksAreExempt(string clientIp)
    {
        await using var host = await StartWithPolicyAsync(o => {
            o.PermitLimit = 1;
            o.ExemptPrivateNetworks = true;
        });

        (await SendManyAsync(host, clientIp, 3)).ShouldAllBe(s => s == HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow("10.1.2.3")]
    [DataRow("127.0.0.1")]
    [DataRow("::1")]
    [DataRow("fd12:3456:789a::1")]
    [DataRow("::ffff:192.168.1.1")]
    [DataRow("none")]
    public async Task InternalClients_AreLimitedByDefault(string clientIp)
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 1);

        (await SendManyAsync(host, clientIp, 2)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [TestMethod]
    [DataRow("X-Forwarded-For", "198.51.100.7")]
    [DataRow("Forwarded", "for=198.51.100.7")]
    [DataRow("X-Real-IP", "198.51.100.7")]
    [DataRow("CF-Connecting-IP", "198.51.100.7")]
    public async Task ForwardedRequestFromPrivateAddress_IsLoggedOnce(string header, string value)
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 10);

        await SendAsync(host, "10.0.0.5", "/limited", (header, value));
        await SendAsync(host, "::1", "/limited", (header, value));

        var warning = host.WarningLogs.ShouldHaveSingleItem();
        warning.Message.ShouldContain($"A request from 10.0.0.5 has a {header} header");
    }

    [TestMethod]
    public async Task ForwardedRequestFromPrivateAddress_IsLogged_WhenPrivateNetworksAreExempt()
    {
        await using var host = await StartWithPolicyAsync(o => {
            o.PermitLimit = 10;
            o.ExemptPrivateNetworks = true;
        });

        await SendAsync(host, "127.0.0.1", "/limited", ("X-Forwarded-For", "198.51.100.7"));

        host.WarningLogs.ShouldHaveSingleItem().Message.ShouldContain("X-Forwarded-For");
    }

    [TestMethod]
    public async Task ForwardedRequestFromPublicAddress_OrPrivateRequestWithoutForwarding_IsNotLogged()
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 10);

        await SendAsync(host, ClientA, "/limited", ("X-Forwarded-For", "198.51.100.7"));
        await SendAsync(host, "none", "/limited", ("X-Forwarded-For", "198.51.100.7"));
        await SendAsync(host, "10.0.0.5");

        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ForwardedRequest_IsNotLogged_WhenLimitIsDisabled()
    {
        await using var host = await StartWithPolicyAsync(o => {
            o.PermitLimit = 10;
            o.Enabled = false;
        });

        await SendAsync(host, "10.0.0.5", "/limited", ("X-Forwarded-For", "198.51.100.7"));

        host.WarningLogs.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("11.0.0.1")]
    [DataRow("172.15.255.255")]
    [DataRow("172.32.0.1")]
    [DataRow("192.169.0.1")]
    [DataRow("100.63.255.255")]
    [DataRow("100.128.0.1")]
    [DataRow("2001:db8::1")]
    [DataRow("fec0::1")]
    [DataRow("::ffff:203.0.113.5")]
    public async Task PublicClients_AreLimited(string clientIp)
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 1);

        (await SendManyAsync(host, clientIp, 2)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [TestMethod]
    [DataRow("203.0.113.7", true)]
    [DataRow("::ffff:203.0.113.7", true)]
    [DataRow("203.0.113.8", false)]
    [DataRow("198.51.100.200", true)]
    [DataRow("198.51.101.1", false)]
    [DataRow("2001:db8:1234:5::1", true)]
    [DataRow("2001:db8:1235::1", false)]
    public async Task ExemptNetworks_AreNotLimited(string clientIp, bool exempt)
    {
        await using var host = await StartWithPolicyAsync(o => {
            o.PermitLimit = 1;
            o.ExemptNetworks.Add("203.0.113.7");
            o.ExemptNetworks.Add(" 198.51.100.0/24 ");
            o.ExemptNetworks.Add("2001:db8:1234::/48");
        });

        var statuses = await SendManyAsync(host, clientIp, 2);

        statuses.ShouldBe(exempt ? [HttpStatusCode.OK, HttpStatusCode.OK] : [HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [TestMethod]
    public async Task Options_CanBeBoundFromConfiguration()
    {
        const string json = """
            {
              "RateLimits": {
                "ExemptNetworks": [ "203.0.113.7/32", "2001:db8:1234::/48" ],
                "Window": "00:05:00"
              }
            }
            """;

        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var section = configuration.GetSection("RateLimits");

        var bound = new ClientRateLimitOptions();
        section.Bind(bound);
        bound.ExemptNetworks.ShouldBe(["203.0.113.7/32", "2001:db8:1234::/48"]);
        bound.Window.ShouldBe(TimeSpan.FromMinutes(5));

        await using var host = await StartWithPolicyAsync(o => {
            section.Bind(o);
            o.PermitLimit = 1;
        });

        (await SendManyAsync(host, "203.0.113.7", 2)).ShouldAllBe(s => s == HttpStatusCode.OK);
        (await SendManyAsync(host, "2001:db8:1234:1::1", 2)).ShouldAllBe(s => s == HttpStatusCode.OK);
        (await SendManyAsync(host, ClientA, 2)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [TestMethod]
    public async Task Disabled_NeverLimits()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimits:Enabled"] = "false" })
            .Build();

        await using var host = await StartWithPolicyAsync(o => {
            configuration.GetSection("RateLimits").Bind(o);
            o.PermitLimit = 1;
        });

        (await SendManyAsync(host, ClientA, 3)).ShouldAllBe(s => s == HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task Rejection_UsesApiErrorFormatWithRetryAfter()
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 1);

        await SendAsync(host, ClientA);
        var response = await SendAsync(host, ClientA);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Content.Headers.ContentType!.ToString().ShouldBe("text/plain; charset=utf-8; format=error-code");
        (await response.Content.ReadAsStringAsync()).ShouldBe("[rate-limited] Too many requests. Please wait a moment and try again.");

        var retryAfter = response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull();
        retryAfter.ShouldBeGreaterThan(TimeSpan.Zero);
        retryAfter.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
    }

    [TestMethod]
    public async Task Rejection_RetryAfterCountsDownToTheNextWindow()
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 1);

        // Windows are aligned to the clock, so the time left in the current one is known.
        long windowTicks = TimeSpan.FromMinutes(1).Ticks;
        var remaining = TimeSpan.FromTicks(windowTicks - (Clock.GetUtcNow().UtcTicks % windowTicks));

        await SendAsync(host, ClientA);
        var response = await SendAsync(host, ClientA);

        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds)));
    }

    [TestMethod]
    public async Task Rejection_UsesCustomMessage()
    {
        await using var host = await StartAsync(o => o
            .AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 1)
            .SetApiRejectionResponse("Too many attempts. Please wait a few minutes and try again."));

        await SendAsync(host, ClientA);
        var response = await SendAsync(host, ClientA);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await response.Content.ReadAsStringAsync()).ShouldBe("[rate-limited] Too many attempts. Please wait a few minutes and try again.");
    }

    [TestMethod]
    public async Task Rejection_UsesTheMessageOfTheLimit()
    {
        await using var host = await StartAsync(o => o
            .AddClientFixedWindowLimiter(Policy, c => {
                c.PermitLimit = 1;
                c.RejectionMessage = "Too many emails were sent. Please try again in an hour.";
            })
            .SetClientFixedWindowGlobalLimiter(c => c.PermitLimit = 3)
            .SetApiRejectionResponse("Too many attempts. Please wait a moment and try again."));

        await SendAsync(host, ClientA);
        var byPolicy = await SendAsync(host, ClientA);

        await SendManyAsync(host, ClientB, 3, "/other");
        var byGlobalLimiter = await SendAsync(host, ClientB, "/other");

        (await byPolicy.Content.ReadAsStringAsync()).ShouldBe("[rate-limited] Too many emails were sent. Please try again in an hour.");
        (await byGlobalLimiter.Content.ReadAsStringAsync()).ShouldBe("[rate-limited] Too many attempts. Please wait a moment and try again.");
    }

    [TestMethod]
    public async Task Rejection_UsesTheMessageOfTheFunction_WithTheMessageOfTheLimitAndTheRetryTime()
    {
        await using var host = await StartAsync(o => o
            .AddClientFixedWindowLimiter(Policy, c => {
                c.PermitLimit = 1;
                c.RejectionMessage = "emails";
                c.Window = TimeSpan.FromHours(1);
            })
            .SetClientFixedWindowGlobalLimiter(permitLimit: 3, TimeSpan.FromMinutes(1))
            .SetApiRejectionResponse(rejection => {
                var request = rejection.HttpContext.Request;
                int seconds = (int)Math.Ceiling(rejection.RetryAfter.ShouldNotBeNull().TotalSeconds);

                return $"{request.Path} {rejection.RejectionMessage ?? "(none)"} {request.Headers["X-Lang"]} {seconds}";
            }));

        await SendAsync(host, ClientA);
        var byPolicy = await SendAsync(host, ClientA, "/limited", ("X-Lang", "fr"));

        await SendManyAsync(host, ClientB, 3, "/other");
        var byGlobalLimiter = await SendAsync(host, ClientB, "/other", ("X-Lang", "de"));

        // The function gets the time that the Retry-After header has, before rounding: the time until the next window of the limit.
        byPolicy.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        int policySeconds = (int)byPolicy.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().TotalSeconds;
        int globalSeconds = (int)byGlobalLimiter.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().TotalSeconds;
        policySeconds.ShouldBeGreaterThan(60);
        globalSeconds.ShouldBeLessThanOrEqualTo(60);

        (await byPolicy.Content.ReadAsStringAsync()).ShouldBe($"[rate-limited] /limited emails fr {policySeconds}");
        (await byGlobalLimiter.Content.ReadAsStringAsync()).ShouldBe($"[rate-limited] /other (none) de {globalSeconds}");
    }

    [TestMethod]
    public async Task ShortOverload_SetsLimitAndWindow()
    {
        var window = TimeSpan.FromHours(1);
        await using var host = await StartAsync(o => o.AddClientFixedWindowLimiter(Policy, permitLimit: 2, window));

        (await SendManyAsync(host, ClientA, 3)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);

        var remaining = TimeSpan.FromTicks(window.Ticks - (Clock.GetUtcNow().UtcTicks % window.Ticks));
        Clock.Advance(remaining - TimeSpan.FromSeconds(1));
        (await SendAsync(host, ClientA)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await SendAsync(host, ClientA)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task GlobalLimiterShortOverload_SetsLimitAndWindow()
    {
        var window = TimeSpan.FromHours(1);
        await using var host = await StartAsync(o => o.SetClientFixedWindowGlobalLimiter(permitLimit: 2, window), requirePolicy: false);

        (await SendManyAsync(host, ClientA, 3, "/other")).ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);

        var remaining = TimeSpan.FromTicks(window.Ticks - (Clock.GetUtcNow().UtcTicks % window.Ticks));
        Clock.Advance(remaining - TimeSpan.FromSeconds(1));
        (await SendAsync(host, ClientA, "/other")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await SendAsync(host, ClientA, "/other")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public void ShortOverloads_InvalidArguments_Throw()
    {
        var options = new RateLimiterOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.AddClientFixedWindowLimiter(Policy, permitLimit: 0, TimeSpan.FromMinutes(1)));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddClientFixedWindowLimiter(Policy, permitLimit: 1, TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => options.SetClientFixedWindowGlobalLimiter(permitLimit: 0, TimeSpan.FromMinutes(1)));
        Should.Throw<ArgumentOutOfRangeException>(() => options.SetClientFixedWindowGlobalLimiter(permitLimit: 1, TimeSpan.Zero));
    }

    [TestMethod]
    public async Task SharedConfiguration_AppliesToLimitsAddedAfterIt_BeforeTheirOwn()
    {
        await using var host = await StartAsync(o => o
            .AddClientFixedWindowLimiter("before", c => c.PermitLimit = 1)
            .ConfigureClientLimits(c => {
                c.PermitLimit = 1;
                c.ExemptNetworks.Add(ClientB);
            })
            .AddClientFixedWindowLimiter(Policy, permitLimit: 2, TimeSpan.FromMinutes(1))
            .SetClientFixedWindowGlobalLimiter(_ => { }));

        // The policy has its own limit of 2, and the global limiter the shared limit of 1, so the second request to the other endpoint is refused. Client
        // B is exempt from both.

        (await SendManyAsync(host, ClientA, 2)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await SendManyAsync(host, ClientB, 3)).ShouldAllBe(s => s == HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task Rejection_UsesApiErrorFormat_WithoutRejectionResponseSetUp()
    {
        await using var policyHost = await StartAsync(o => o.AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 1));
        await using var globalHost = await StartAsync(o => o.SetClientFixedWindowGlobalLimiter(c => c.PermitLimit = 1), requirePolicy: false);

        foreach (var host in new[] { policyHost, globalHost })
        {
            await SendAsync(host, ClientA);
            var response = await SendAsync(host, ClientA);

            response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await response.Content.ReadAsStringAsync()).ShouldBe("[rate-limited] Too many requests. Please wait a moment and try again.");
            response.Headers.RetryAfter.ShouldNotBeNull();
        }
    }

    [TestMethod]
    public async Task Rejection_KeepsAHandlerSetBefore()
    {
        await using var customHandlerHost = await StartAsync(o => {
            o.OnRejected = (context, _) => {
                context.HttpContext.Response.StatusCode = StatusCodes.Status418ImATeapot;
                return ValueTask.CompletedTask;
            };

            o.AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 1);
        });

        await using var customMessageHost = await StartAsync(o => o
            .SetApiRejectionResponse("Slow down.")
            .AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 1));

        (await SendManyAsync(customHandlerHost, ClientA, 2)).ShouldBe([HttpStatusCode.OK, (HttpStatusCode)StatusCodes.Status418ImATeapot]);

        await SendAsync(customMessageHost, ClientA);
        (await (await SendAsync(customMessageHost, ClientA)).Content.ReadAsStringAsync()).ShouldBe("[rate-limited] Slow down.");
    }

    [TestMethod]
    public async Task Window_ResetsTheLimit()
    {
        await using var host = await StartWithPolicyAsync(o => {
            o.PermitLimit = 1;
            o.Window = TimeSpan.FromSeconds(10);
        });

        (await SendManyAsync(host, ClientA, 2)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);

        Clock.Advance(TimeSpan.FromSeconds(10));

        (await SendManyAsync(host, ClientA, 2)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [TestMethod]
    public async Task Queue_HoldsRequestsOverTheLimitUntilTheNextWindow()
    {
        await using var host = await StartWithPolicyAsync(o => {
            o.PermitLimit = 1;
            o.QueueLimit = 1;
            o.Window = TimeSpan.FromSeconds(1);
        });

        (await SendAsync(host, ClientA)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // One of two requests over the limit waits in the queue, and the other is rejected.
        var first = SendAsync(host, ClientA);
        var second = SendAsync(host, ClientA);
        var rejected = await Task.WhenAny(first, second);
        var queued = rejected == first ? second : first;

        (await rejected).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        queued.IsCompleted.ShouldBeFalse();

        Clock.Advance(TimeSpan.FromSeconds(1));

        (await queued.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task GlobalLimiter_LimitsEveryEndpointPerClient()
    {
        await using var host = await StartAsync(
            o => o.SetClientFixedWindowGlobalLimiter(c => {
                c.PermitLimit = 2;
                c.ExemptPrivateNetworks = true;
            }).SetApiRejectionResponse(),
            requirePolicy: false);

        (await SendAsync(host, ClientA, "/limited")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(host, ClientA, "/other")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var rejected = await SendAsync(host, ClientA, "/other");
        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull();

        (await SendManyAsync(host, ClientB, 2, "/other")).ShouldAllBe(s => s == HttpStatusCode.OK);
        (await SendManyAsync(host, "10.0.0.1", 5, "/other")).ShouldAllBe(s => s == HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task GlobalLimiter_AppliesWithEndpointPolicies()
    {
        await using var host = await StartAsync(o => o
            .SetClientFixedWindowGlobalLimiter(c => c.PermitLimit = 10)
            .AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 1)
            .SetApiRejectionResponse());

        (await SendManyAsync(host, ClientA, 2, "/limited")).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await SendManyAsync(host, ClientA, 2, "/other")).ShouldAllBe(s => s == HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task GlobalLimiter_CountsRequestsRefusedByEndpointPolicyOnce()
    {
        await using var host = await StartAsync(o => o
            .SetClientFixedWindowGlobalLimiter(c => c.PermitLimit = 3)
            .AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 1)
            .SetApiRejectionResponse());

        (await SendManyAsync(host, ClientA, 2, "/limited")).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await SendManyAsync(host, ClientA, 2, "/other")).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [TestMethod]
    public async Task Limit_CountsConcurrentRequestsExactly()
    {
        await using var host = await StartWithPolicyAsync(o => o.PermitLimit = 5);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SendAsync(host, ClientA)));

        responses.Count(r => r.StatusCode is HttpStatusCode.OK).ShouldBe(5);
        responses.Count(r => r.StatusCode is HttpStatusCode.TooManyRequests).ShouldBe(15);
    }

    [TestMethod]
    public void PermitLimitNotSet_Throws()
    {
        var options = new RateLimiterOptions();

        Should.Throw<InvalidOperationException>(() => options.AddClientFixedWindowLimiter(Policy, _ => { })).Message.ShouldContain("PermitLimit");
        Should.Throw<InvalidOperationException>(() => options.SetClientFixedWindowGlobalLimiter(_ => { })).Message.ShouldContain("PermitLimit");
    }

    [TestMethod]
    [DataRow("not an address")]
    [DataRow("203.0.113.0/33")]
    [DataRow("")]
    public void InvalidExemptNetwork_Throws(string entry)
    {
        var options = new RateLimiterOptions();

        Should.Throw<FormatException>(() => options.AddClientFixedWindowLimiter(Policy, o => {
            o.PermitLimit = 1;
            o.ExemptNetworks.Add(entry);
        })).Message.ShouldContain($"'{entry}'");
    }

    [TestMethod]
    public void Options_RejectInvalidValues()
    {
        var options = new ClientRateLimitOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.PermitLimit = -1);
        Should.Throw<ArgumentOutOfRangeException>(() => options.Window = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.QueueLimit = -1);
    }

    [TestMethod]
    [DataRow("203.0.113.10", "203.0.113.10")]
    [DataRow("::ffff:203.0.113.10", "203.0.113.10")]
    [DataRow("2001:db8:1:2::1", "2001:db8:1:2::")]
    [DataRow("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::")]
    [DataRow("2001:db8:1:3::1", "2001:db8:1:3::")]
    [DataRow("fe80::1%5", "fe80::")]
    public void GetClientKey_ReturnsTheKeyOfTheAddress(string address, string expected)
    {
        ClientRateLimitOptions.GetClientKey(IPAddress.Parse(address)).ShouldBe(IPAddress.Parse(expected));
    }

    [TestMethod]
    public void GetClientKey_GroupsIpv6AddressesPerSlash64()
    {
        var key = ClientRateLimitOptions.GetClientKey(IPAddress.Parse("2001:db8:1:2::1"));

        ClientRateLimitOptions.GetClientKey(IPAddress.Parse("2001:db8:1:2:abcd::2")).ShouldBe(key);
        ClientRateLimitOptions.GetClientKey(IPAddress.Parse("2001:db8:1:3::1")).ShouldNotBe(key);
        ClientRateLimitOptions.GetClientKey(IPAddress.Parse("2001:db8:2:2::1")).ShouldNotBe(key);
    }

    [TestMethod]
    public void GetClientKey_ReturnsAnyAddress_WithoutAnAddress()
    {
        ClientRateLimitOptions.GetClientKey(null).ShouldBe(IPAddress.Any);
    }

    [TestMethod]
    public void Options_Defaults()
    {
        var options = new ClientRateLimitOptions();

        options.Enabled.ShouldBeTrue();
        options.PermitLimit.ShouldBe(0);
        options.Window.ShouldBe(TimeSpan.FromMinutes(1));
        options.QueueLimit.ShouldBe(0);
        options.ExemptPrivateNetworks.ShouldBeFalse();
        options.ExemptNetworks.ShouldBeEmpty();
    }
}

[PrefixTestClass]
public sealed class ClientRateLimitTestsWithMemoryStore : ClientRateLimitTests
{
    protected override StoreKind StoreKind => StoreKind.Memory;

    [TestMethod]
    public async Task EachServer_CountsSeparately()
    {
        await using var server1 = await StartWithPolicyAsync(o => o.PermitLimit = 1);
        await using var server2 = await StartWithPolicyAsync(o => o.PermitLimit = 1);

        (await SendAsync(server1, ClientA)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(server2, ClientA)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(server1, ClientA)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [TestMethod]
    public async Task OwnGlobalLimiter_CountsEachRequestOnce()
    {
        // The policy counts synchronously with the memory store, so ASP.NET Core never takes a second permit from a global limiter of its own kind.
        await using var host = await StartAsync(o => {
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter(
                "all", _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromHours(1) }));

            o.AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 10).SetApiRejectionResponse();
        });

        (await SendManyAsync(host, ClientA, 4)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [TestMethod]
    public async Task RegisteredMemoryStore_CountsRequests()
    {
        var store = new MemoryShortLivedStore(Clock);

        await using var host = await TestWebHost.StartAsync(
            app => {
                app.Use((context, next) => {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ClientA);
                    return next(context);
                });

                app.UseRateLimiter();
                app.MapGet("/limited", () => "ok").RequireRateLimiting("limit");
            },
            configureServices: services => {
                services.AddSingleton<IShortLivedStore>(store);
                services.AddSingleton<TimeProvider>(Clock);
                services.AddRateLimiter(o => o.AddClientFixedWindowLimiter("limit", c => c.PermitLimit = 2).SetApiRejectionResponse());
            });

        (await SendManyAsync(host, ClientA, 3)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await store.GetAsync(GetWindowKey("httpapi:rate-limit:limit:" + ClientA, TimeSpan.FromMinutes(1)))).ShouldNotBeNull();
    }

    [TestMethod]
    public async Task WithoutRegisteredStore_LimitsShareOneMemoryStore()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.Use((context, next) => {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ClientA);
                    return next(context);
                });

                app.UseRateLimiter();
                app.MapGet("/first", () => "ok").RequireRateLimiting("first");
                app.MapGet("/second", () => "ok").RequireRateLimiting("second");
            },
            configureServices: services => {
                services.AddSingleton<TimeProvider>(Clock);

                // Room for one count (about 230 bytes), but not for a second one.
                services.Configure<MemoryShortLivedStoreOptions>(o => o.MaxSize = 300);
                services.AddRateLimiter(o => o
                    .AddClientFixedWindowLimiter("first", permitLimit: 10, TimeSpan.FromMinutes(1))
                    .AddClientFixedWindowLimiter("second", permitLimit: 10, TimeSpan.FromMinutes(1)));
            });

        (await host.Client.GetAsync("/first")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client.GetAsync("/second")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [TestMethod]
    public async Task FullStore_RefusesRequestsItCannotCount()
    {
        // Room for one client's count (about 230 bytes), but not for a second one.
        var options = Options.Create(new MemoryShortLivedStoreOptions { MaxSize = 300 });
        var store = new MemoryShortLivedStore(Clock, options, NullLogger<MemoryShortLivedStore>.Instance);

        await using var host = await StartAsync(o => o.AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 10), store: store);

        (await SendManyAsync(host, ClientA, 2)).ShouldAllBe(s => s == HttpStatusCode.OK);

        var response = await SendAsync(host, ClientB);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldBe(
            "[short-lived-store-full] The service is too busy to handle this request. Please try again in a minute.");
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromMinutes(1));
    }

    private string GetWindowKey(string prefix, TimeSpan window) => $"{prefix}:{Clock.GetUtcNow().UtcTicks / window.Ticks}";
}

[PrefixTestClass]
public sealed class ClientRateLimitTestsWithSharedStore : ClientRateLimitTests
{
    protected override StoreKind StoreKind => StoreKind.Shared;

    [TestMethod]
    public async Task Limit_HoldsAcrossServers()
    {
        await using var server1 = await StartWithPolicyAsync(o => o.PermitLimit = 3);
        await using var server2 = await StartWithPolicyAsync(o => o.PermitLimit = 3);

        (await SendAsync(server1, ClientA)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(server2, ClientA)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(server1, ClientA)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(server2, ClientA)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await SendAsync(server1, ClientA)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Other clients have their own counts, on every server.
        (await SendAsync(server2, ClientB)).StatusCode.ShouldBe(HttpStatusCode.OK);

        Clock.Advance(TimeSpan.FromMinutes(1));
        (await SendAsync(server2, ClientA)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task ConcurrentRequestsAcrossServers_AreCountedExactly()
    {
        await using var server1 = await StartWithPolicyAsync(o => o.PermitLimit = 5);
        await using var server2 = await StartWithPolicyAsync(o => o.PermitLimit = 5);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => SendAsync(i % 2 is 0 ? server1 : server2, ClientA)));

        responses.Count(r => r.StatusCode is HttpStatusCode.OK).ShouldBe(5);
    }

    [TestMethod]
    public async Task ConcurrencyGlobalLimiter_WorksWithClientPolicies()
    {
        await using var host = await StartAsync(o => {
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetConcurrencyLimiter(
                "all", _ => new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 }));

            o.AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 3).SetApiRejectionResponse();
        });

        (await SendManyAsync(host, ClientA, 4)).ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await SendAsync(host, ClientB)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task UnavailableStore_RefusesRequestsWithItsError()
    {
        var store = new InterceptingShortLivedStore(SharedStore) {
            Before = (_, _) => throw new ServiceUnavailableApiException("The store is busy.") { RetryAfter = TimeSpan.FromSeconds(5) },
        };

        await using var host = await StartAsync(
            o => o.SetClientFixedWindowGlobalLimiter(c => c.PermitLimit = 10), requirePolicy: false, store: store);

        var response = await SendAsync(host, ClientA);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldBe("The store is busy.");
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task GlobalAndPolicyLimits_AreCountedUnderTheirOwnKeys()
    {
        await using var host = await StartAsync(o => o
            .SetClientFixedWindowGlobalLimiter(c => c.PermitLimit = 10)
            .AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 10)
            .SetApiRejectionResponse());

        await SendAsync(host, ClientA);

        long window = Clock.GetUtcNow().UtcTicks / TimeSpan.FromMinutes(1).Ticks;

        SharedStore.Keys.Order().ShouldBe([
            $"httpapi:rate-limit-global:{ClientA}:{window}",
            $"httpapi:rate-limit:{Policy}:{ClientA}:{window}",
        ]);

        (await SharedStore.GetAsync($"httpapi:rate-limit:{Policy}:{ClientA}:{window}")).ShouldBe("1");
    }

    [TestMethod]
    public async Task Limits_CountClientsUnderTheirClientKeys()
    {
        await using var host = await StartAsync(o => o
            .SetClientFixedWindowGlobalLimiter(c => c.PermitLimit = 10)
            .AddClientFixedWindowLimiter(Policy, c => c.PermitLimit = 10)
            .SetApiRejectionResponse());

        string[] clientIps = [ClientA, "::ffff:" + ClientB, "2001:db8:1:2::1", "2001:db8:1:2:ffff::2", "2001:db8:1:3::1", "none"];

        foreach (string clientIp in clientIps)
            (await SendAsync(host, clientIp)).StatusCode.ShouldBe(HttpStatusCode.OK);

        long window = Clock.GetUtcNow().UtcTicks / TimeSpan.FromMinutes(1).Ticks;

        var clientKeys = clientIps.Select(ip => ClientRateLimitOptions.GetClientKey(ip is "none" ? null : IPAddress.Parse(ip))).Distinct().ToList();
        clientKeys.Count.ShouldBe(5);

        SharedStore.Keys.Order().ShouldBe(clientKeys
            .SelectMany(key => new[] { $"httpapi:rate-limit-global:{key}:{window}", $"httpapi:rate-limit:{Policy}:{key}:{window}" })
            .Order());
    }
}
