using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.Client;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// A sign-in or a sign-out of a native API client while a request of the same session runs, against a service served by Kestrel: whichever of the two
/// responses arrives first, the client ends up with the sign-in's session, or without a session after the sign-out.
/// </summary>
[PrefixTestClass]
public sealed class NativeSignInOrderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task ResponseThatDeletesTheReplacedSessionsCookie_BeforeTheSignInsResponse_KeepsTheNewSession()
    {
        await using var host = await Host.StartAsync();
        var client = new Client(host.BaseAddress);
        await client.SignInAsync(1);

        // The sign-in ends the session and waits before it responds. A request that validates the session meanwhile fails, and deletes the cookie.
        var signIn = client.SignInAsync(2, hold: "after");
        await host.Gate("after").Arrived.Task.WaitAsync(Timeout);
        await Should.ThrowAsync<UnauthorizedApiException>(() => client.GetSessionIdAsync(forceValidate: true));
        client.CurrentSessionToken.ShouldBeNull();

        host.Gate("after").Release.SetResult();
        await signIn.WaitAsync(Timeout);

        (await client.GetSessionIdAsync()).ShouldBe(2);
        client.SessionEndedCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task ResponseThatRefreshesTheSessionsCookie_BeforeTheSignOutsResponse_EndsTheSession()
    {
        await using var host = await Host.StartAsync();
        var client = new Client(host.BaseAddress);
        await client.SignInAsync(1);
        string firstToken = client.CurrentSessionToken.ShouldNotBeNull();

        // The cookie is due, and a request refreshes it before the sign-out, which waits before it signs out, ends the session.
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        var signOut = client.SignOutAsync(hold: "before");
        await host.Gate("before").Arrived.Task.WaitAsync(Timeout);
        (await client.GetSessionIdAsync()).ShouldBe(1);
        client.CurrentSessionToken.ShouldNotBe(firstToken);

        host.Gate("before").Release.SetResult();
        await signOut.WaitAsync(Timeout);

        client.CurrentSessionToken.ShouldBeNull();
        client.SessionEndedCount.ShouldBe(1);
        await Should.ThrowAsync<UnauthorizedApiException>(() => client.GetSessionIdAsync());
    }

    [TestMethod]
    public async Task ResponseThatRefreshesTheReplacedSessionsCookie_BeforeTheSignInsResponse_KeepsTheNewSession()
    {
        await using var host = await Host.StartAsync();
        var client = new Client(host.BaseAddress);
        await client.SignInAsync(1);
        string firstToken = client.CurrentSessionToken.ShouldNotBeNull();

        // The cookie is due, and a request refreshes it before the sign-in, which waits before it signs in, ends the session.
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        var signIn = client.SignInAsync(2, hold: "before");
        await host.Gate("before").Arrived.Task.WaitAsync(Timeout);
        (await client.GetSessionIdAsync()).ShouldBe(1);
        client.CurrentSessionToken.ShouldNotBe(firstToken);

        host.Gate("before").Release.SetResult();
        await signIn.WaitAsync(Timeout);

        (await client.GetSessionIdAsync()).ShouldBe(2);
    }

    [TestMethod]
    public async Task ResponseThatDeletesTheReplacedSessionsCookie_AfterTheSignInsResponse_IsIgnored()
    {
        await using var host = await Host.StartAsync();
        var client = new Client(host.BaseAddress);
        await client.SignInAsync(1);

        // A request of the first session waits while the sign-in replaces it, and then validates the session, which fails and deletes the cookie.
        var held = client.GetHeldSessionIdAsync();
        await host.Gate("held").Arrived.Task.WaitAsync(Timeout);
        await client.SignInAsync(2);
        host.Gate("held").Release.SetResult();
        await Should.ThrowAsync<UnauthorizedApiException>(() => held.WaitAsync(Timeout));

        (await client.GetSessionIdAsync()).ShouldBe(2);
        client.SessionEndedCount.ShouldBe(0);
    }

    private sealed class Gate
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentDictionary<string, Gate> _gates = [];
        private long _lastSessionId;

        private Host(WebApplication app, ManualClock clock)
        {
            _app = app;
            Clock = clock;
        }

        public Uri BaseAddress => new(_app.Urls.First() + "/");

        public ManualClock Clock { get; }

        public Gate Gate(string name) => _gates.GetOrAdd(name, _ => new Gate());

        public static async Task<Host> StartAsync()
        {
            var clock = new ManualClock();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
            builder.Services.AddSingleton<InMemorySessionStore>();
            builder.Services.AddApiResponseHandling();
            builder.Services.AddApiExceptionHandler();
            builder.Services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();

            var app = builder.Build();
            var host = new Host(app, clock);

            app.MapPost("/sign-in/{userId:int}", async (HttpContext context, int userId, string? hold, InMemorySessionStore store) => {
                if (hold is "before")
                    await host.HoldAsync(hold);

                long sessionId = Interlocked.Increment(ref host._lastSessionId);
                store.UserStamps.TryAdd(userId, 1);

                await context.GetRequiredSessionContext<TestSessionToken>().SignInAsync(persistent: true, info => {
                    store.Sessions[sessionId] = new TestSessionData {
                        Id = sessionId,
                        UserId = userId,
                        Device = info.Device,
                        IpAddress = info.IpAddress,
                        RefreshedUtc = info.RefreshedUtc,
                        ExpiresUtc = info.ExpiresUtc,
                        Generation = info.Generation,
                        IsPersistent = info.IsPersistent,
                        CreatedUtc = info.CreatedUtc,
                    };

                    return Task.FromResult(new TestSessionToken(sessionId, userId, Stamp: 1));
                });

                if (hold is "after")
                    await host.HoldAsync(hold);
            });

            app.MapPost("/sign-out", async (HttpContext context, string? hold) => {
                if (hold is "before")
                    await host.HoldAsync(hold);

                await context.GetRequiredSessionContext<TestSessionToken>().SignOutAsync();
            });

            app.MapGet("/session", async Task<long> (HttpContext context, bool forceValidate) => {
                var options = SessionAccessOptions.OptionalUserIdPrecondition | (forceValidate ? SessionAccessOptions.ForceValidate : default);
                return (await context.GetRequiredSessionTokenAsync<TestSessionToken>(options)).SessionId;
            });

            app.MapGet("/held-session", async Task<long> (HttpContext context) => {
                await host.HoldAsync("held");

                var options = SessionAccessOptions.OptionalUserIdPrecondition | SessionAccessOptions.ForceValidate;
                return (await context.GetRequiredSessionTokenAsync<TestSessionToken>(options)).SessionId;
            });

            app.UseApiResponseHandling();
            await app.StartAsync();

            return host;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        private async Task HoldAsync(string gate)
        {
            Gate(gate).Arrived.TrySetResult();
            await Gate(gate).Release.Task.WaitAsync(Timeout);
        }
    }

    private sealed class Client(Uri baseAddress) : ApiClientBase()
    {
        private int _sessionEndedCount;

        public int SessionEndedCount => Volatile.Read(ref _sessionEndedCount);

        public string? CurrentSessionToken => SessionToken;

        protected override string UserAgent => "NativeClient/1.0";

        public Task SignInAsync(int userId, string? hold = null) => PostAsync($"sign-in/{userId}", null, hold is null ? default : [("hold", hold)]);

        public Task<long> GetSessionIdAsync(bool forceValidate = false) => GetAsync<long>("session", [("forceValidate", forceValidate)]);

        public Task<long> GetHeldSessionIdAsync() => GetAsync<long>("held-session");

        public Task SignOutAsync(string? hold = null) => PostAsync("sign-out", null, hold is null ? default : [("hold", hold)]);

        protected override Uri GetBaseAddress() => baseAddress;

        protected override void OnSessionEnded() => Interlocked.Increment(ref _sessionEndedCount);
    }
}
