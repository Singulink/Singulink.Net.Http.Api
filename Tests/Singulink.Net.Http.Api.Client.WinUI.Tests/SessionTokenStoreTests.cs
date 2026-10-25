namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class SessionTokenStoreTests
{
    private const string Key = "SessionToken";

    [TestMethod]
    public void StoredToken_IsReadBack_EncryptedOnWindows()
    {
        var settings = new Dictionary<string, object>();
        var store = CreateStore(settings);

        store.Write("the-session-token");

        store.Read().ShouldBe("the-session-token");

        if (OperatingSystem.IsWindows())
            settings[Key].ShouldBeOfType<string>().ShouldNotContain("the-session-token");
        else
            settings[Key].ShouldBe("the-session-token");
    }

    [TestMethod]
    [DataRow("not base64!")]
    [DataRow("dGhlLXNlc3Npb24tdG9rZW4=")]
    public void UnencryptedToken_IsDroppedOnWindows(string unencrypted)
    {
        var settings = new Dictionary<string, object> { [Key] = unencrypted };

        string? token = CreateStore(settings).Read();

        if (OperatingSystem.IsWindows())
        {
            token.ShouldBeNull();
            settings.ShouldNotContainKey(Key);
        }
        else
        {
            token.ShouldBe(unencrypted);
        }
    }

    [TestMethod]
    public void NullToken_RemovesStoredToken()
    {
        var settings = new Dictionary<string, object>();
        var store = CreateStore(settings);
        store.Write("the-session-token");

        store.Write(null);

        settings.ShouldNotContainKey(Key);
        store.Read().ShouldBeNull();
    }

    [TestMethod]
    public void Name_IsTheSettingsKey()
    {
        var settings = new Dictionary<string, object>();
        var store = new SessionTokenStore("OtherService", null, () => settings);

        store.Write("token");

        store.Name.ShouldBe("OtherService");
        settings.Keys.ShouldBe(["OtherService"]);
        CreateStore(settings).Read().ShouldBeNull();
    }

    [TestMethod]
    public void FailedReadAndWrite_AreReportedInsteadOfThrown()
    {
        var errors = new List<Exception>();
        var store = new SessionTokenStore(Key, errors.Add, () => throw new InvalidOperationException("The settings are not available."));

        store.Read().ShouldBeNull();
        store.Write("token");
        store.Write(null);

        errors.Count.ShouldBe(3);
        errors.ShouldAllBe(e => e is InvalidOperationException);
    }

    [TestMethod]
    public void FailureWithoutErrorHandler_IsIgnored()
    {
        var store = new SessionTokenStore(Key, null, () => throw new InvalidOperationException("The settings are not available."));

        store.Read().ShouldBeNull();
        store.Write("token");
    }

    [TestMethod]
    public void EmptyName_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => new SessionTokenStore(string.Empty));
    }

    [TestMethod]
    [DataRow("Session/Token")]
    [DataRow("Session\\Token")]
    [DataRow("Session:Token")]
    [DataRow("Session*")]
    [DataRow("Session?")]
    [DataRow("\"Session\"")]
    [DataRow("<Session>")]
    [DataRow("Session|Token")]
    [DataRow("Session\tToken")]
    [DataRow("CON")]
    [DataRow("nul")]
    [DataRow("Com1")]
    [DataRow("LPT9")]
    [DataRow("AUX.token")]
    [DataRow("PRN .token")]
    public void NameThatIsNotAWindowsFileName_ThrowsArgumentException(string name)
    {
        Should.Throw<ArgumentException>(() => new SessionTokenStore(name)).ParamName.ShouldBe("name");
    }

    [TestMethod]
    [DataRow("SessionToken")]
    [DataRow("My Service.Session-Token_2")]
    [DataRow("Console")]
    [DataRow("COM10")]
    [DataRow("Sitzungstoken für Ünïcode")]
    public void NameThatIsAWindowsFileName_IsAccepted(string name)
    {
        new SessionTokenStore(name).Name.ShouldBe(name);
    }

    [TestMethod]
    public void FolderPath_DefaultsToNull()
    {
        var store = new SessionTokenStore();
        store.FolderPath.ShouldBeNull();

        store = new SessionTokenStore { FolderPath = Path.Combine(Path.GetTempPath(), "MyApp") };
        store.FolderPath.ShouldBe(Path.Combine(Path.GetTempPath(), "MyApp"));
    }

    private static SessionTokenStore CreateStore(Dictionary<string, object> settings) => new(Key, null, () => settings);
}
