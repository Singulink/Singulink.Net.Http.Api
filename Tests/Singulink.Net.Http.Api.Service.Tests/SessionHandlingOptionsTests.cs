namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class SessionHandlingOptionsTests
{
    [TestMethod]
    public void Defaults()
    {
        var options = new SessionHandlingOptions();

        options.SessionCookieName.ShouldBe("session-token");
        options.CookieDomain.ShouldBeNull();
        options.CookiePath.ShouldBe("/");
        options.UserIdPreconditionQueryName.ShouldBe("if-userId");
        options.ForcedAccessOptions.ShouldBe(SessionAccessOptions.None);
        options.MultipleRefreshGracePeriod.ShouldBe(TimeSpan.FromMinutes(2));
        options.TempSessionExpiry.ShouldBe(TimeSpan.FromDays(1));
        options.PersistentSessionExpiry.ShouldBe(TimeSpan.FromDays(30));
    }

    [TestMethod]
    public void EmptyNames_Throw()
    {
        var options = new SessionHandlingOptions();

        Should.Throw<ArgumentException>(() => options.SessionCookieName = string.Empty);
        Should.Throw<ArgumentException>(() => options.CookiePath = string.Empty);
        Should.Throw<ArgumentException>(() => options.UserIdPreconditionQueryName = string.Empty);
    }

    [TestMethod]
    public void UndefinedForcedAccessOptions_Throw()
    {
        var options = new SessionHandlingOptions();

        Should.Throw<ArgumentException>(() => options.ForcedAccessOptions = (SessionAccessOptions)64);
        options.ForcedAccessOptions.ShouldBe(SessionAccessOptions.None);

        options.ForcedAccessOptions = SessionAccessOptions.ForceValidate | SessionAccessOptions.AllowAllOrigins;
        options.ForcedAccessOptions.ShouldBe(SessionAccessOptions.ForceValidate | SessionAccessOptions.AllowAllOrigins);
    }

    [TestMethod]
    public void RegisteredDefaults_MatchNewInstance()
    {
        var host = new SessionTestHost();

        host.Options.MultipleRefreshGracePeriod.ShouldBe(TimeSpan.FromMinutes(2));
        host.Options.SessionCookieName.ShouldBe("session-token");
    }
}
