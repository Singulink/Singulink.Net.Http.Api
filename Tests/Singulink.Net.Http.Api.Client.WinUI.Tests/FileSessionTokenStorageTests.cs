using Singulink.IO;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class FileSessionTokenStorageTests
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Singulink.Net.Http.Api.Tests", Guid.NewGuid().ToString("N"));

    [TestMethod]
    public void StoredToken_IsReadBack_EncryptedOnWindows()
    {
        string path = GetPath();

        FileSessionTokenStorage.Write(ToFile(path), "the-session-token");

        FileSessionTokenStorage.Read(ToFile(path)).ShouldBe("the-session-token");
        Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("the-session-token").ShouldBe(!OperatingSystem.IsWindows());
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [TestMethod]
    public void NewToken_ReplacesStoredToken()
    {
        string path = GetPath();
        FileSessionTokenStorage.Write(ToFile(path), "the-session-token");

        FileSessionTokenStorage.Write(ToFile(path), "the-new-token");

        FileSessionTokenStorage.Read(ToFile(path)).ShouldBe("the-new-token");
    }

    [TestMethod]
    public void MissingFileOrFolder_ReadsAsNull()
    {
        FileSessionTokenStorage.Read(ToFile(GetPath())).ShouldBeNull();
        FileSessionTokenStorage.Read(ToFile(Path.Combine(_folder, "Missing", "SessionToken.dat"))).ShouldBeNull();
    }

    [TestMethod]
    public void NullToken_RemovesStoredToken()
    {
        string path = GetPath();
        FileSessionTokenStorage.Write(ToFile(path), "the-session-token");

        FileSessionTokenStorage.Write(ToFile(path), null);
        FileSessionTokenStorage.Write(ToFile(Path.Combine(_folder, "Missing", "SessionToken.dat")), null);

        File.Exists(path).ShouldBeFalse();
        FileSessionTokenStorage.Read(ToFile(path)).ShouldBeNull();
    }

    [TestMethod]
    public void UnencryptedToken_IsDroppedOnWindows()
    {
        string path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("the-session-token"));

        string? token = FileSessionTokenStorage.Read(ToFile(path));

        if (OperatingSystem.IsWindows())
        {
            token.ShouldBeNull();
            File.Exists(path).ShouldBeFalse();
        }
        else
        {
            token.ShouldBe("the-session-token");
        }
    }

    private static IAbsoluteFilePath ToFile(string path) => FilePath.ParseAbsolute(path, PathOptions.None);

    private string GetPath() => Path.Combine(_folder, "SessionToken.dat");

    [TestCleanup]
    public void DeleteFolder()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
