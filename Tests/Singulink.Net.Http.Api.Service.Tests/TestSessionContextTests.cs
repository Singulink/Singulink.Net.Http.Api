using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Testing;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The test session context of the testing package, which apps test endpoint code with.
/// </summary>
[PrefixTestClass]
public sealed class TestSessionContextTests
{
    private static readonly TestSessionToken Token = new(SessionId: 1, UserId: 7, Stamp: 1);

    [TestMethod]
    public async Task Token_IsReturned_WithTheUserIdPreconditionOfItsUser()
    {
        var context = new TestSessionContext<TestSessionToken>(Token);

        context.UserIdPrecondition.ShouldBe("7");
        (await context.GetTokenAsync()).ShouldBeSameAs(Token);
        (await context.GetRequiredTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBeSameAs(Token);
    }

    [TestMethod]
    public async Task NoSession_RequiredTokenThrowsSignInRequired()
    {
        var context = new TestSessionContext<TestSessionToken>();

        (await context.GetTokenAsync()).ShouldBeNull();
        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => context.GetRequiredTokenAsync().AsTask());
        ex.ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
    }

    [TestMethod]
    public async Task UserIdPrecondition_IsChecked()
    {
        var context = new TestSessionContext<TestSessionToken>(Token) { UserIdPrecondition = "8" };

        (await Should.ThrowAsync<UserChangedApiException>(() => context.GetTokenAsync().AsTask())).ExpectedUserId.ShouldBe("8");

        context.UserIdPrecondition = null;

        await Should.ThrowAsync<UserRequiredApiException>(() => context.GetTokenAsync().AsTask());
        (await context.GetTokenAsync(SessionAccessOptions.OptionalUserIdPrecondition)).ShouldBeSameAs(Token);
    }

    [TestMethod]
    public async Task OriginNotAllowed_IsRefused_UnlessAllOriginsAreAllowed()
    {
        var context = new TestSessionContext<TestSessionToken>(Token) { IsOriginAllowed = false };

        (await Should.ThrowAsync<ForbiddenApiException>(() => context.GetTokenAsync().AsTask())).ErrorCode.ShouldBe(ApiErrorCodes.OriginBlocked);
        await Should.ThrowAsync<ForbiddenApiException>(() => context.SignInAsync(false, _ => Task.FromResult(Token)));
        (await context.GetTokenAsync(SessionAccessOptions.AllowAllOrigins)).ShouldBeSameAs(Token);
    }

    [TestMethod]
    public async Task SignIn_PassesSignInInfo_AndKeepsTheToken()
    {
        var clock = new TestClock();
        var options = new SessionHandlingOptions { TempSessionExpiry = TimeSpan.FromHours(2), MaxTempSessionLifetime = TimeSpan.FromHours(1) };
        var context = new TestSessionContext<TestSessionToken>(device: "Windows (Edge 140)", ipAddress: IPAddress.Loopback) {
            Options = options,
            TimeProvider = clock,
        };

        var token = await context.SignInAsync(persistent: false, info => {
            info.Device.ShouldBe("Windows (Edge 140)");
            info.IpAddress.ShouldBe(IPAddress.Loopback);
            info.CreatedUtc.ShouldBe(clock.GetUtcNow().UtcDateTime);
            info.ExpiresUtc.ShouldBe(clock.GetUtcNow().UtcDateTime.AddHours(1));
            info.IsPersistent.ShouldBeFalse();

            return Task.FromResult(Token);
        });

        token.ShouldBeSameAs(Token);
        context.Token.ShouldBeSameAs(Token);
        context.LastSignIn.ShouldNotBeNull().Generation.ShouldBe(0);
    }

    [TestMethod]
    public async Task SignInWithCode_RunsTheSessionCreationFunction()
    {
        var signIn = new SignInTestContext(StoreKind.Memory);
        var pkce = Client.Pkce.Create();
        string code = await signIn.Codes.CreateAsync("7", pkce.Challenge, appData: "version:3");
        var context = new TestSessionContext<TestSessionToken>();

        var token = await signIn.Codes.SignInAsync(context, new RedeemSignInCodeCommand(code, pkce.Verifier, isPersistent: true), (redeemed, info) => {
            redeemed.UserId.ShouldBe("7");
            redeemed.AppData.ShouldBe("version:3");
            info.IsPersistent.ShouldBeTrue();

            return Task.FromResult(Token);
        });

        token.ShouldBeSameAs(Token);
    }

    [TestMethod]
    public async Task SignOut_EndsTheSession()
    {
        var context = new TestSessionContext<TestSessionToken>(Token);

        await context.SignOutAsync();

        context.Token.ShouldBeNull();
        context.IsSignedOut.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => context.SetToken(Token));
    }
}
