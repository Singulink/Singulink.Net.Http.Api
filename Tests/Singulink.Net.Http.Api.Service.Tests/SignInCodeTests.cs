using System.Globalization;
using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Sign-in code tests, run against the memory store and against a shared store with a new service instance for every call.
/// </summary>
public abstract class SignInCodeTests
{
    protected abstract StoreKind StoreKind { get; }

    protected SignInTestContext CreateContext(Action<SignInCodeOptions>? configureCodes = null) => new(StoreKind, configureCodes);

    [TestMethod]
    public async Task Redeem_WithMatchingVerifier_ReturnsUserIdOnce()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();

        string code = await context.Codes.CreateAsync("user-1", pkce.Challenge);

        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe("user-1");
        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    public async Task Redeem_WithWrongVerifier_ReturnsNullAndUsesUpCode()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();

        string code = await context.Codes.CreateAsync("user-1", pkce.Challenge);

        (await context.Codes.RedeemAsync(code, Client.Pkce.Create().Verifier)).ShouldBeNull();
        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    public async Task Redeem_WithMalformedVerifier_ReturnsNullAndUsesUpCode()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();

        string code = await context.Codes.CreateAsync("user-1", pkce.Challenge);

        (await context.Codes.RedeemAsync(code, string.Empty)).ShouldBeNull();
        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("not a code, but as long as one is: 43 chars")]
    public async Task Redeem_UnknownCode_ReturnsNull(string unknownCode)
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();
        await context.Codes.CreateAsync("user-1", pkce.Challenge);

        (await context.Codes.RedeemAsync(unknownCode, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("""{"userId":"user-1"}""")]
    [DataRow("""{"userId":null,"codeChallenge":"E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"}""")]
    public async Task UnreadableCode_IsTreatedAsMissingAndLogged(string storedValue)
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();
        string code = await context.Codes.CreateAsync("user-1", pkce.Challenge);

        // As if the code were stored by a version that kept its state differently.
        await context.Store.TakeAsync(ToolkitKeys.SignInCode + code);
        await context.Store.TryAddAsync(ToolkitKeys.SignInCode + code, storedValue, TimeSpan.FromMinutes(1));

        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldBeNull();
        context.Logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be read", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Codes_AreIndependent()
    {
        var context = CreateContext();
        var pkce1 = Client.Pkce.Create();
        var pkce2 = Client.Pkce.Create();

        string code1 = await context.Codes.CreateAsync("user-1", pkce1.Challenge);
        string code2 = await context.Codes.CreateAsync("user-2", pkce2.Challenge);

        code1.ShouldNotBe(code2);
        (await context.Codes.RedeemAsync(code2, pkce1.Verifier)).ShouldBeNull();
        (await context.Codes.RedeemAsync(code1, pkce1.Verifier)).ShouldNotBeNull().UserId.ShouldBe("user-1");
    }

    [TestMethod]
    public async Task Redeem_AfterDefaultLifetime_ReturnsNull()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();

        string validCode = await context.Codes.CreateAsync("user-1", pkce.Challenge);
        string expiredCode = await context.Codes.CreateAsync("user-1", pkce.Challenge);

        context.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        (await context.Codes.RedeemAsync(validCode, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe("user-1");

        context.Clock.Advance(TimeSpan.FromSeconds(1));
        (await context.Codes.RedeemAsync(expiredCode, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    public async Task Redeem_AfterConfiguredLifetime_ReturnsNull()
    {
        var context = CreateContext(o => o.Lifetime = TimeSpan.FromSeconds(30));
        var pkce = Client.Pkce.Create();

        string code = await context.Codes.CreateAsync("user-1", pkce.Challenge);

        context.Clock.Advance(TimeSpan.FromSeconds(30));
        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    public async Task Redeem_UnderConcurrency_SucceedsOnce()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();
        const int rounds = 100;
        var codes = new string[rounds];

        for (int i = 0; i < rounds; i++)
            codes[i] = await context.Codes.CreateAsync($"user-{i}", pkce.Challenge);

        var results = Concurrently.RunTasks(threads: 8, rounds, round => context.Codes.RedeemAsync(codes[round], pkce.Verifier));

        for (int round = 0; round < rounds; round++)
            results[round].Where(r => r is not null).ShouldHaveSingleItem()!.UserId.ShouldBe($"user-{round}");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("too-short")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-c")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM=")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw/cM")]
    public async Task Create_WithMalformedChallenge_ThrowsBadRequest(string challenge)
    {
        var context = CreateContext();

        var ex = await Should.ThrowAsync<BadRequestApiException>(() => context.Codes.CreateAsync("user-1", challenge));
        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public void Options_DefaultsAndValidation()
    {
        new SignInCodeOptions().Lifetime.ShouldBe(TimeSpan.FromMinutes(10));
        new DeviceLinkOptions().Lifetime.ShouldBe(TimeSpan.FromMinutes(10));

        Should.Throw<ArgumentOutOfRangeException>(() => new SignInCodeOptions().Lifetime = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => new DeviceLinkOptions().Lifetime = TimeSpan.FromSeconds(-1));
    }

    [TestMethod]
    public async Task SignIn_ValidCode_CreatesSessionForCodeUser()
    {
        var context = CreateContext();
        var host = new SessionTestHost();
        var request = host.CreateRequest(ipAddress: SessionTestHost.OtherIp);
        var pkce = Client.Pkce.Create();

        string code = await context.Codes.CreateAsync("7", pkce.Challenge);
        string? sessionUserId = null;
        SignInInfo? signInInfo = null;

        var token = await context.Codes.SignInAsync(request.Session, code, pkce.Verifier, persistent: true, (redeemed, info) => {
            sessionUserId = redeemed.UserId;
            signInInfo = info;
            return Task.FromResult(
                host.CreateSession(sessionId: 3, userId: int.Parse(redeemed.UserId, CultureInfo.InvariantCulture), persistent: info.IsPersistent).Token);
        });

        sessionUserId.ShouldBe("7");
        signInInfo.ShouldNotBeNull();
        signInInfo.IsPersistent.ShouldBeTrue();
        signInInfo.IpAddress.ShouldBe(SessionTestHost.OtherIp);
        token.UserId.ShouldBe(7);
        request.IssuedSession.ShouldNotBeNull().Token.ShouldBe(token);

        // The code is used up.
        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SignIn_WithRedeemCommand_UsesItsValues(bool persistent)
    {
        var context = CreateContext();
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        var pkce = Client.Pkce.Create();

        string code = await context.Codes.CreateAsync("7", pkce.Challenge);
        var command = new RedeemSignInCodeCommand(code, pkce.Verifier, persistent);

        var token = await context.Codes.SignInAsync(request.Session, command, (redeemed, info) => {
            redeemed.UserId.ShouldBe("7");
            info.IsPersistent.ShouldBe(persistent);
            return Task.FromResult(host.CreateSession(sessionId: 3, userId: 7, persistent: info.IsPersistent).Token);
        });

        request.IssuedSession.ShouldNotBeNull().IsPersistent.ShouldBe(persistent);
        token.UserId.ShouldBe(7);
        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("sign-in-version:3")]
    public async Task Redeem_ReturnsAppData(string? appData)
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();

        string code = await context.Codes.CreateAsync("user-1", pkce.Challenge, appData);

        var redeemed = (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldNotBeNull();
        redeemed.UserId.ShouldBe("user-1");
        redeemed.AppData.ShouldBe(appData);
    }

    [TestMethod]
    public async Task SignIn_PassesAppDataToSessionCreation()
    {
        var context = CreateContext();
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        var pkce = Client.Pkce.Create();
        string code = await context.Codes.CreateAsync("7", pkce.Challenge, appData: "v2");
        string? appData = null;

        await context.Codes.SignInAsync(request.Session, code, pkce.Verifier, persistent: false, (redeemed, _) => {
            appData = redeemed.AppData;
            return Task.FromResult(host.CreateSession(userId: 7).Token);
        });

        appData.ShouldBe("v2");
    }

    [TestMethod]
    public async Task SignIn_PassesTheCancellationTokenToTheStore()
    {
        var context = new SignInTestContext(StoreKind, wrapStore: store => new InterceptingShortLivedStore(store));
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        var pkce = Client.Pkce.Create();
        string code = await context.Codes.CreateAsync("7", pkce.Challenge);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => context.Codes.SignInAsync(
            request.Session,
            code,
            pkce.Verifier,
            persistent: false,
            (_, _) => Task.FromResult(host.CreateSession(userId: 7).Token),
            cancellationToken: cancellation.Token));

        // The code was not taken.
        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe("7");
    }

    [TestMethod]
    public async Task SignIn_DisallowedOrigin_ThrowsForbidden_AndLeavesCodeRedeemable()
    {
        var context = CreateContext();
        var host = new SessionTestHost();
        var request = host.CreateRequest(origin: "https://evil.test");
        var pkce = Client.Pkce.Create();
        string code = await context.Codes.CreateAsync("7", pkce.Challenge);

        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => context.Codes.SignInAsync(
            request.Session,
            code,
            pkce.Verifier,
            persistent: false,
            (_, _) => Task.FromResult(host.CreateSession(userId: 7).Token)));

        ex.ErrorCode.ShouldBe(ApiErrorCodes.OriginBlocked);

        // The code was not taken.
        (await context.Codes.RedeemAsync(code, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe("7");
    }

    [TestMethod]
    public async Task SignIn_InvalidCode_ThrowsUnauthorized_WithoutCreatingSession()
    {
        var context = CreateContext();
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        var pkce = Client.Pkce.Create();
        bool sessionCreated = false;

        string code = await context.Codes.CreateAsync("7", pkce.Challenge);

        Task<TestSessionToken> CreateSession(RedeemedSignInCode redeemed, SignInInfo info)
        {
            sessionCreated = true;
            return Task.FromResult(host.CreateSession(userId: 7).Token);
        }

        // Wrong verifier, which also uses up the code, then the right verifier on the used code.

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(
            () => context.Codes.SignInAsync(request.Session, code, Client.Pkce.Create().Verifier, persistent: false, CreateSession));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.ErrorCode.ShouldBe(SignInErrorCodes.SignInCodeInvalid);
        ex.Message.ShouldBe("The sign-in code is invalid or expired. Please sign in again.");

        await Should.ThrowAsync<UnauthorizedApiException>(
            () => context.Codes.SignInAsync(request.Session, code, pkce.Verifier, persistent: false, CreateSession));

        await Should.ThrowAsync<UnauthorizedApiException>(
            () => context.Codes.SignInAsync(request.Session, "unknown", pkce.Verifier, persistent: false, CreateSession));

        sessionCreated.ShouldBeFalse();
        request.SessionCookies.ShouldBeEmpty();
    }
}

[PrefixTestClass]
public sealed class SignInCodeTestsWithMemoryStore : SignInCodeTests
{
    protected override StoreKind StoreKind => StoreKind.Memory;
}

[PrefixTestClass]
public sealed class SignInCodeTestsWithSharedStore : SignInCodeTests
{
    protected override StoreKind StoreKind => StoreKind.Shared;

    [TestMethod]
    public async Task Code_IsKeptUnderToolkitKeyAsJson()
    {
        var context = CreateContext();
        var store = (SharedShortLivedStore)context.Store;
        var pkce = Client.Pkce.Create();

        string code = await context.Codes.CreateAsync("user-1", pkce.Challenge);

        string key = store.Keys.ShouldHaveSingleItem();
        key.ShouldBe("httpapi:sign-in-code:" + code);
        (await store.GetAsync(key)).ShouldBe($$"""{"userId":"user-1","codeChallenge":"{{pkce.Challenge}}"}""");
    }

    [TestMethod]
    public async Task CodeWithAppData_IsKeptWithItsAppData()
    {
        var context = CreateContext();
        var store = (SharedShortLivedStore)context.Store;
        var pkce = Client.Pkce.Create();

        await context.Codes.CreateAsync("user-1", pkce.Challenge, appData: "v2");

        string key = store.Keys.ShouldHaveSingleItem();
        (await store.GetAsync(key)).ShouldBe($$"""{"userId":"user-1","codeChallenge":"{{pkce.Challenge}}","appData":"v2"}""");
    }
}
