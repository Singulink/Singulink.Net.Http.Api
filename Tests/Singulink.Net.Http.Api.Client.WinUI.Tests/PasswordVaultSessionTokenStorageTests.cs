using Windows.Security.Credentials;

#pragma warning disable Uno0001 // The password vault is only implemented on Android and iOS: the tests use Uno's vault with its contents in memory.

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Tests the Android storage, Uno Platform's password vault, with Uno's vault logic and its contents in memory in place of Uno's file, which the Android
/// Keystore encrypts.
/// </summary>
[PrefixTestClass]
public sealed class PasswordVaultSessionTokenStorageTests
{
    private const string Resource = "Singulink.Net.Http.Api.SessionTokens";

    [TestMethod]
    public void StoredToken_IsReadBack_AndReplaced()
    {
        var data = new VaultData();
        var storage = CreateStorage("SessionToken", data);

        storage.Read().ShouldBeNull();

        storage.Write("the-session-token");
        storage.Read().ShouldBe("the-session-token");

        storage.Write("the-new-token");
        storage.Read().ShouldBe("the-new-token");
        new MemoryVault(data).RetrieveAll().ShouldHaveSingleItem().UserName.ShouldBe("SessionToken");
    }

    [TestMethod]
    public void NullToken_RemovesOnlyTheStoredToken()
    {
        var data = new VaultData();
        new MemoryVault(data).Add(new PasswordCredential("OtherResource", "SessionToken", "other-password"));
        var storage = CreateStorage("SessionToken", data);
        CreateStorage("OtherService", data).Write("other-token");
        storage.Write("the-session-token");

        storage.Write(null);

        storage.Read().ShouldBeNull();
        storage.Write(null);

        new MemoryVault(data).RetrieveAll().Select(c => (c.Resource, c.UserName))
            .ShouldBe([("OtherResource", "SessionToken"), (Resource, "OtherService")], ignoreOrder: true);
    }

    [TestMethod]
    public void Name_IsTheUserName_ComparedIgnoringCase()
    {
        var data = new VaultData();
        CreateStorage("SessionToken", data).Write("the-session-token");

        CreateStorage("sessiontoken", data).Read().ShouldBe("the-session-token");
        CreateStorage("OtherService", data).Read().ShouldBeNull();
    }

    [TestMethod]
    public void SessionTokenStore_MovesSettingsTokenIntoTheVault()
    {
        var data = new VaultData();
        var settings = new Dictionary<string, object> { ["SessionToken"] = "the-old-token" };

        if (OperatingSystem.IsWindows())
        {
            // Encrypted on Windows, where the tests also run; Android keeps it unencrypted.
            settings.Clear();
            SettingsSessionTokenStorage.Write(settings, "SessionToken", "the-old-token");
        }

        var store = new SessionTokenStore("SessionToken", null, () => settings, CreateStorage("SessionToken", data));

        store.Read().ShouldBe("the-old-token");

        settings.ShouldBeEmpty();
        CreateStorage("SessionToken", data).Read().ShouldBe("the-old-token");
    }

    private static PasswordVaultSessionTokenStorage CreateStorage(string name, VaultData data) => new(name, () => new MemoryVault(data));

    /// <summary>
    /// The contents of a vault in memory, in Uno's format, which every <see cref="MemoryVault"/> created with it shares.
    /// </summary>
    private sealed class VaultData
    {
        public byte[]? Data { get; set; }
    }

    /// <summary>
    /// Uno's password vault with a data in memory. Each instance loads what the data has, as each vault that the storage creates loads the file.
    /// </summary>
    private sealed class MemoryVault(VaultData data) : PasswordVault(new MemoryPersister(data))
    {
        private sealed class MemoryPersister(VaultData data) : IPersister
        {
            public bool TryOpenRead(out Stream inputStream)
            {
                inputStream = data.Data is null ? Stream.Null : new MemoryStream(data.Data);
                return data.Data is not null;
            }

            public WriteTransaction OpenWrite(out Stream outputStream)
            {
                var stream = new MemoryStream();
                outputStream = stream;

                return new WriteTransaction(onCommit: () => data.Data = stream.ToArray());
            }
        }
    }
}
