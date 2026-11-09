using Singulink.IO;

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

        var folder = DirectoryPath.GetTemp().CombineDirectory("MyApp", PathOptions.None);
        store = new SessionTokenStore { FolderPath = folder };
        store.FolderPath.ShouldBeSameAs(folder);
    }

    [TestMethod]
    public void NoPlatformStorage_OnThisPlatform()
    {
        // The tests run on the plain .NET build, which Uno Platform apps with the Skia renderer also load on iOS and Android, where it finds their storage.
        SessionTokenStoreNative.IsSupported.ShouldBeFalse();
        PlatformSessionTokenStorage.Create(Key).ShouldBeNull();
    }

    [TestMethod]
    public void PlatformStorage_IsTheKeychainOnIOS_AndThePasswordVaultOnAndroid()
    {
        PlatformSessionTokenStorage.Create(Key, isKeychainSupported: true, isAndroid: false).ShouldBeOfType<KeychainSessionTokenStorage>();
        PlatformSessionTokenStorage.Create(Key, isKeychainSupported: false, isAndroid: true).ShouldBeOfType<PasswordVaultSessionTokenStorage>();
        PlatformSessionTokenStorage.Create(Key, isKeychainSupported: false, isAndroid: false).ShouldBeNull();
    }

    [TestMethod]
    public void NativeStorage_KeepsTheToken_NotTheSettings()
    {
        var settings = new Dictionary<string, object>();
        var native = new FakeNativeStorage();
        var store = new SessionTokenStore(Key, null, () => settings, native);

        store.Write("the-session-token");

        native.Token.ShouldBe("the-session-token");
        settings.ShouldBeEmpty();
        store.Read().ShouldBe("the-session-token");

        store.Write(null);

        native.Token.ShouldBeNull();
        store.Read().ShouldBeNull();
    }

    [TestMethod]
    public void SettingsToken_IsMovedToNativeStorage()
    {
        var settings = CreateSettings("the-old-token");
        var native = new FakeNativeStorage();
        var store = new SessionTokenStore(Key, null, () => settings, native);

        store.Read().ShouldBe("the-old-token");

        native.Token.ShouldBe("the-old-token");
        settings.ShouldNotContainKey(Key);

        store.Read().ShouldBe("the-old-token");
        native.Writes.ShouldBe(1);
    }

    [TestMethod]
    public void SettingsToken_UnderTheStoreName_IsMoved()
    {
        var settings = new Dictionary<string, object>();
        SettingsSessionTokenStorage.Write(settings, "OtherService", "the-old-token");
        SettingsSessionTokenStorage.Write(settings, Key, "another-token");
        var native = new FakeNativeStorage();

        new SessionTokenStore("OtherService", null, () => settings, native).Read().ShouldBe("the-old-token");

        native.Token.ShouldBe("the-old-token");
        settings.Keys.ShouldBe([Key]);
    }

    [TestMethod]
    public void NativeToken_WinsOverSettingsToken()
    {
        var settings = CreateSettings("the-old-token");
        var native = new FakeNativeStorage { Token = "the-current-token" };
        var store = new SessionTokenStore(Key, null, () => settings, native);

        store.Read().ShouldBe("the-current-token");

        native.Writes.ShouldBe(0);
    }

    [TestMethod]
    public void Write_RemovesSettingsToken()
    {
        // A settings token next to a native one, such as one a failed move left behind, would otherwise come back once the session ends.
        var settings = CreateSettings("the-old-token");
        var native = new FakeNativeStorage { Token = "the-current-token" };
        var store = new SessionTokenStore(Key, null, () => settings, native);

        store.Write(null);

        settings.ShouldNotContainKey(Key);
        store.Read().ShouldBeNull();
    }

    [TestMethod]
    public void FailedMove_KeepsSettingsToken_ReportsAndReturnsIt()
    {
        var errors = new List<Exception>();
        var settings = CreateSettings("the-old-token");
        var native = new FakeNativeStorage { FailWrites = true };
        var store = new SessionTokenStore(Key, errors.Add, () => settings, native);

        store.Read().ShouldBe("the-old-token");

        errors.ShouldHaveSingleItem().ShouldBeOfType<InvalidOperationException>();
        settings.ShouldContainKey(Key);
        native.Token.ShouldBeNull();

        // The next read moves it.
        native.FailWrites = false;
        store.Read().ShouldBe("the-old-token");

        native.Token.ShouldBe("the-old-token");
        settings.ShouldNotContainKey(Key);
    }

    [TestMethod]
    public void FailedNativeWrite_KeepsSettingsToken()
    {
        var errors = new List<Exception>();
        var settings = CreateSettings("the-old-token");
        var native = new FakeNativeStorage { FailWrites = true };
        var store = new SessionTokenStore(Key, errors.Add, () => settings, native);

        store.Write("the-new-token");

        errors.ShouldHaveSingleItem();
        settings.ShouldContainKey(Key);
    }

    [TestMethod]
    public void FailedNativeRemoval_StillRemovesSettingsToken()
    {
        var errors = new List<Exception>();
        var settings = CreateSettings("the-old-token");
        var native = new FakeNativeStorage { Token = "the-current-token", FailWrites = true };
        var store = new SessionTokenStore(Key, errors.Add, () => settings, native);

        store.Write(null);

        errors.ShouldHaveSingleItem();
        settings.ShouldNotContainKey(Key);
    }

    [TestMethod]
    public void FailedNativeRead_IsReported_SettingsTokenNotMoved()
    {
        var errors = new List<Exception>();
        var settings = CreateSettings("the-old-token");
        var native = new FakeNativeStorage { FailReads = true };

        new SessionTokenStore(Key, errors.Add, () => settings, native).Read().ShouldBeNull();

        errors.ShouldHaveSingleItem();
        settings.ShouldContainKey(Key);
        native.Writes.ShouldBe(0);
    }

    private static SessionTokenStore CreateStore(Dictionary<string, object> settings) => new(Key, null, () => settings);

    private static Dictionary<string, object> CreateSettings(string token)
    {
        // Encrypted on Windows, where the tests also run; iOS and Android keep it unencrypted.
        var settings = new Dictionary<string, object>();
        SettingsSessionTokenStorage.Write(settings, Key, token);

        return settings;
    }

    private sealed class FakeNativeStorage : ISessionTokenStorage
    {
        public string? Token { get; set; }

        public bool FailReads { get; set; }

        public bool FailWrites { get; set; }

        public int Writes { get; private set; }

        public string? Read() => FailReads ? throw new InvalidOperationException("The keychain is locked.") : Token;

        public void Write(string? token)
        {
            if (FailWrites)
                throw new InvalidOperationException("The keychain is locked.");

            Token = token;
            Writes++;
        }
    }
}
