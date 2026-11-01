namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Tests the plain .NET build, which reports that it has no native storage, so that callers fall back to their own.
/// </summary>
[PrefixTestClass]
public sealed class SessionTokenStoreNativeTests
{
    [TestMethod]
    public void IsSupported_IsFalse()
    {
        SessionTokenStoreNative.IsSupported.ShouldBeFalse();
    }

    [TestMethod]
    public void ReadAndWrite_ThrowPlatformNotSupported()
    {
        Should.Throw<PlatformNotSupportedException>(() => SessionTokenStoreNative.Read("SessionToken"));
        Should.Throw<PlatformNotSupportedException>(() => SessionTokenStoreNative.Write("SessionToken", "token"));
        Should.Throw<PlatformNotSupportedException>(() => SessionTokenStoreNative.Write("SessionToken", null));
    }

    [TestMethod]
    public void EmptyName_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => SessionTokenStoreNative.Read(string.Empty)).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => SessionTokenStoreNative.Write(string.Empty, "token")).ParamName.ShouldBe("name");
    }
}
