using System.Reflection;
using System.Runtime.InteropServices;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class UserAgentTests
{
    private static readonly string LibraryProduct = $"Singulink.Net.Http.Api.Client/{typeof(ApiClientBase).Assembly.GetName().Version}";

    [TestMethod]
    public async Task Request_HasDefaultUserAgent()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new TestApiClient(handler);

        await client.SendRequestAsync(client.CreateGetRequest("items"));

        string expected = ApiClientBase.CreateDefaultUserAgent(Assembly.GetEntryAssembly()?.GetName(), RuntimeInformation.OSDescription);
        handler.SingleRequest.UserAgent.ShouldBe(expected);
    }

    [TestMethod]
    public void Default_IsEntryAssemblyAndOperatingSystem()
    {
        var name = new AssemblyName("MyApp") { Version = new Version(1, 2, 3, 0) };
        string userAgent = ApiClientBase.CreateDefaultUserAgent(name, "Microsoft Windows 10.0.26100");

        userAgent.ShouldBe("MyApp/1.2.3.0 (Microsoft Windows 10.0.26100)");
    }

    [TestMethod]
    public void Default_WithoutEntryAssembly_IsLibrary()
    {
        ApiClientBase.CreateDefaultUserAgent(null, "Linux 6.8").ShouldBe($"{LibraryProduct} (Linux 6.8)");
    }

    [TestMethod]
    public void Default_EntryAssemblyNameThatIsNotAToken_IsLibrary()
    {
        ApiClientBase.CreateDefaultUserAgent(new AssemblyName("My App"), "Linux 6.8").ShouldBe($"{LibraryProduct} (Linux 6.8)");
    }

    [TestMethod]
    public void Default_OperatingSystemCharsThatCommentsCantHave_AreReplaced()
    {
        var name = new AssemblyName("MyApp") { Version = new Version(1, 0) };

        ApiClientBase.CreateDefaultUserAgent(name, "Darwin (unbalanced\\x").ShouldBe("MyApp/1.0 (Darwin [unbalanced/x)");
        ApiClientBase.CreateDefaultUserAgent(name, "  ").ShouldBe("MyApp/1.0");
    }

    [TestMethod]
    public void Default_NonAsciiInOperatingSystem_IsReplaced()
    {
        var name = new AssemblyName("MyApp") { Version = new Version(1, 0) };

        ApiClientBase.CreateDefaultUserAgent(name, "Linux café").ShouldBe("MyApp/1.0 (Linux caf?)");
    }

    [TestMethod]
    public void Create_IsProductAndOperatingSystem()
    {
        string expected = ApiClientBase.CreateUserAgent("MyApp", "2.1.0-beta+5", RuntimeInformation.OSDescription);

        TestApiClient.CreateAppUserAgent("MyApp", "2.1.0-beta+5").ShouldBe(expected);
        ApiClientBase.CreateUserAgent("MyApp", "2.1.0-beta+5", "Linux 6.8").ShouldBe("MyApp/2.1.0-beta+5 (Linux 6.8)");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("My App")]
    [DataRow("MyApp/2")]
    [DataRow("MyApp(1)")]
    [DataRow("Café")]
    public void Create_ProductNameThatIsNotAToken_Throws(string productName)
    {
        Should.Throw<ArgumentException>(() => TestApiClient.CreateAppUserAgent(productName, "1.0")).ParamName.ShouldBe("productName");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1.0 beta")]
    [DataRow("1/0")]
    [DataRow("1;0")]
    public void Create_ProductVersionThatIsNotAToken_Throws(string productVersion)
    {
        Should.Throw<ArgumentException>(() => TestApiClient.CreateAppUserAgent("MyApp", productVersion)).ParamName.ShouldBe("productVersion");
    }

    [TestMethod]
    public void Default_IsInherited()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent);

        child.CurrentUserAgent.ShouldBe(parent.CurrentUserAgent);
    }
}
