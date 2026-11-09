using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The options of the directory store of chunked uploads: binding them from a configuration, the friendly names that the directory must have, the checks of
/// the sharing mode, and the required values that the store checks when the application starts.
/// </summary>
[PrefixTestClass]
public sealed class DirectoryUploadStoreOptionsTests
{
    [TestMethod]
    public void DirectoryStoreOptions_BindTheDirectoryAndTheSharingModeFromAConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SharingMode"] = "ProcessLocal" })
            .Build();

        var options = configuration.Get<DirectoryUploadStoreOptions>().ShouldNotBeNull();
        (options.Directory, options.SharingMode).ShouldBe((null, StoreSharingMode.ProcessLocal));

        // The reflection-based binder creates the directory's path from the string with the type converter of Singulink.IO.FileSystem, which reads a
        // directory without a trailing separator too.
        string path = UploadTestHost.CreateDirectoryPath();
        var withDirectory = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Directory"] = path, ["SharingMode"] = "Shared" })
            .Build();

        var bound = withDirectory.Get<DirectoryUploadStoreOptions>().ShouldNotBeNull();
        (bound.Directory, bound.SharingMode).ShouldBe((TestPaths.ToDirectory(path), StoreSharingMode.Shared));
    }

    [TestMethod]
    public void DirectoryStoreOptions_BoundFromAConfiguration_WithAnUnfriendlyDirectory_FailWithTheMessageOfTheSetter()
    {
        var directory = DirectoryPath.GetTemp().CombineDirectory("uploads.", PathOptions.None);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Directory"] = directory.PathDisplay, ["SharingMode"] = "Shared" })
            .Build();

        // The binder sets the property with reflection, which wraps the exception of the setter.
        var ex = Should.Throw<System.Reflection.TargetInvocationException>(() => configuration.Get<DirectoryUploadStoreOptions>());
        ex.InnerException.ShouldBeOfType<ArgumentException>().Message.ShouldBe(
            $"DirectoryUploadStoreOptions.Directory '{directory.PathDisplay}' must be a path with friendly names: Entry name ends with a dot. " +
            "(Parameter 'value')");
    }

    [TestMethod]
    [DataRow("uploads ", "Entry name ends with a space.")]
    [DataRow("uploads.", "Entry name ends with a dot.")]
    [DataRow(" uploads", "Entry name starts with a space.")]
    public void DirectoryStoreOptions_RejectADirectoryWithAnUnfriendlyName(string name, string error)
    {
        var unfriendly = DirectoryPath.GetTemp().CombineDirectory(name, PathOptions.None);
        string message = $"DirectoryUploadStoreOptions.Directory '{unfriendly.PathDisplay}' must be a path with friendly names: {error} (Parameter 'value')";

        var friendly = DirectoryPath.GetTemp().CombineDirectory("uploads", PathOptions.None);
        var options = new DirectoryUploadStoreOptions(friendly, StoreSharingMode.ProcessLocal);

        var ex = Should.Throw<ArgumentException>(() => options.Directory = unfriendly);
        (ex.ParamName, ex.Message).ShouldBe(("value", message));
        options.Directory.ShouldBeSameAs(friendly);

        // The constructor names its own parameter.
        ex = Should.Throw<ArgumentException>(() => new DirectoryUploadStoreOptions(unfriendly, StoreSharingMode.ProcessLocal));
        ex.ParamName.ShouldBe("directory");
        ex.Message.ShouldBe(
            $"DirectoryUploadStoreOptions.Directory '{unfriendly.PathDisplay}' must be a path with friendly names: {error} (Parameter 'directory')");

        // A name further up the path counts too.
        var nested = unfriendly.CombineDirectory("uploads", PathOptions.None);
        Should.Throw<ArgumentException>(() => options.Directory = nested).Message.ShouldBe(
            $"DirectoryUploadStoreOptions.Directory '{nested.PathDisplay}' must be a path with friendly names: {error} (Parameter 'value')");

        // A value that was rejected leaves the options without one, so they still fail validation as not set, and frozen options refuse it as they refuse
        // any other value.
        var bound = Activator.CreateInstance<DirectoryUploadStoreOptions>();
        bound.SharingMode = StoreSharingMode.ProcessLocal;
        Should.Throw<ArgumentException>(() => bound.Directory = unfriendly);
        bound.Directory.ShouldBeNull();
        Should.Throw<InvalidOperationException>(bound.Validate).Message.ShouldBe(
            "DirectoryUploadStoreOptions.Directory is not set. It is required, and has no default.");

        options.Freeze();
        Should.Throw<InvalidOperationException>(() => options.Directory = unfriendly).Message.ShouldBe(
            "The DirectoryUploadStoreOptions are in use, so they can no longer be changed.");
    }

    [TestMethod]
    public void DirectoryStoreOptions_RejectADirectoryWithAReservedDeviceNameOnWindows_AndAcceptItElsewhere()
    {
        var directory = DirectoryPath.GetTemp().CombineDirectory("nul", PathOptions.None);

        if (OperatingSystem.IsWindows())
        {
            Should.Throw<ArgumentException>(() => new DirectoryUploadStoreOptions(directory, StoreSharingMode.ProcessLocal)).Message.ShouldBe(
                $"DirectoryUploadStoreOptions.Directory '{directory.PathDisplay}' must be a path with friendly names: Invalid reserved device name in entry " +
                "name 'nul'. (Parameter 'directory')");
        }
        else
        {
            new DirectoryUploadStoreOptions(directory, StoreSharingMode.ProcessLocal).Directory.ShouldBeSameAs(directory);
        }
    }

    [TestMethod]
    public void DirectoryStoreOptions_AcceptADirectoryWithFriendlyNames()
    {
        var directory = DirectoryPath.GetTemp().CombineDirectory("upload data", PathOptions.None).CombineDirectory("v2.uploads", PathOptions.None);
        var options = Activator.CreateInstance<DirectoryUploadStoreOptions>();

        options.Directory = directory;
        options.SharingMode = StoreSharingMode.Shared;

        options.Directory.ShouldBeSameAs(directory);
        options.Validate();
    }

    [TestMethod]
    [DataRow("Directory")]
    [DataRow("SharingMode")]
    public async Task DirectoryStore_WhoseConfigurationLacksARequiredValue_FailsToStart_NamingIt_UnlessTheActionSetsIt(string missing)
    {
        // The compiler doesn't see a required value that a configuration lacks, which reads as not set until the options are validated at startup.
        string path = UploadTestHost.CreateDirectoryPath();
        var values = new Dictionary<string, string?>();

        if (missing is not "Directory")
            values["Uploads:Directory"] = path;

        if (missing is not "SharingMode")
            values["Uploads:SharingMode"] = "ProcessLocal";

        var section = new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("Uploads");

        var ex = await Should.ThrowAsync<OptionsValidationException>(
            () => StartWithDirectoryStoreAsync(services => services.Configure<DirectoryUploadStoreOptions>(section)));

        ex.Message.ShouldBe($"DirectoryUploadStoreOptions.{missing} is not set. It is required, and has no default.");

        // Set in the action that is passed to AddDirectoryUploadStore, the value is there when the options are validated.
        await using (var host = await StartWithDirectoryStoreAsync(
            services => services.Configure<DirectoryUploadStoreOptions>(section),
            options => {
                if (missing is "Directory")
                    options.Directory = TestPaths.ToDirectory(path);
                else
                    options.SharingMode = StoreSharingMode.ProcessLocal;
            }))
        {
            host.App.Services.GetRequiredService<IUploadStore>().SharingMode.ShouldBe(StoreSharingMode.ProcessLocal);

            // The store froze the options when it was created.
            var options = host.App.Services.GetRequiredService<IOptions<DirectoryUploadStoreOptions>>().Value;
            (options.Directory, options.IsFrozen).ShouldBe((TestPaths.ToDirectory(path), true));
            Should.Throw<InvalidOperationException>(() => options.SharingMode = StoreSharingMode.Shared);
        }

        if (System.IO.Directory.Exists(path))
            UploadTestHost.DeleteDirectory(path);
    }

    [TestMethod]
    public async Task DirectoryStore_BindsItsOptionsFromAConfigurationSection()
    {
        string path = UploadTestHost.CreateDirectoryPath();
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Uploads:Directory"] = path, ["Uploads:SharingMode"] = "Shared" })
            .Build()
            .GetSection("Uploads");

        await using (var host = await StartWithDirectoryStoreAsync(services => services.Configure<DirectoryUploadStoreOptions>(section)))
        {
            host.App.Services.GetRequiredService<IUploadStore>().SharingMode.ShouldBe(StoreSharingMode.Shared);

            var options = host.App.Services.GetRequiredService<IOptions<DirectoryUploadStoreOptions>>().Value;
            (options.Directory, options.SharingMode).ShouldBe((TestPaths.ToDirectory(path), StoreSharingMode.Shared));
        }

        if (System.IO.Directory.Exists(path))
            UploadTestHost.DeleteDirectory(path);
    }

    [TestMethod]
    public void DirectoryStoreOptions_ThatLackEveryRequiredValue_ListThemAllWhenTheyAreFrozen()
    {
        var options = Activator.CreateInstance<DirectoryUploadStoreOptions>();

        options.Directory.ShouldBeNull();
        options.SharingMode.ShouldBe(StoreSharingMode.Unspecified);

        options.GetProblems().ShouldBe([
            "DirectoryUploadStoreOptions.Directory is not set. It is required, and has no default.",
            "DirectoryUploadStoreOptions.SharingMode is not set. It is required, and has no default.",
        ]);

        string message = string.Join(" ", options.GetProblems());
        Should.Throw<InvalidOperationException>(options.Validate).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(options.Freeze).Message.ShouldBe(message);

        // The options were not frozen, and can still be set.
        var directory = TestPaths.ToDirectory(UploadTestHost.CreateDirectoryPath());
        options.IsFrozen.ShouldBeFalse();
        options.Directory = directory;
        options.SharingMode = StoreSharingMode.Shared;
        options.Validate();
        options.IsFrozen.ShouldBeFalse();
        options.Freeze();
        options.IsFrozen.ShouldBeTrue();

        Should.Throw<InvalidOperationException>(() => options.Directory = DirectoryPath.GetTemp());
        (options.Directory, options.SharingMode).ShouldBe((directory, StoreSharingMode.Shared));
    }

    [TestMethod]
    public void DirectoryStoreOptions_CheckTheirValues()
    {
        var directory = DirectoryPath.GetTemp();
        var options = new DirectoryUploadStoreOptions(directory, StoreSharingMode.ProcessLocal);

        Should.Throw<ArgumentOutOfRangeException>(() => options.SharingMode = StoreSharingMode.Unspecified);
        Should.Throw<ArgumentOutOfRangeException>(() => options.SharingMode = (StoreSharingMode)3);
        (options.Directory, options.SharingMode).ShouldBe((directory, StoreSharingMode.ProcessLocal));
    }

    [TestMethod]
    [DataRow(StoreSharingMode.Unspecified)]
    [DataRow((StoreSharingMode)3)]
    public void DirectoryStoreOptions_Constructor_RejectsASharingModeThatIsUnspecifiedOrNotAValue(StoreSharingMode sharingMode)
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(() => new DirectoryUploadStoreOptions(DirectoryPath.GetTemp(), sharingMode));

        ex.ParamName.ShouldBe("sharingMode");
        ex.ActualValue.ShouldBe(sharingMode);
        ex.Message.ShouldStartWith("The value is not ProcessLocal or Shared. (Parameter 'sharingMode')");
    }

    /// <summary>
    /// Starts a host with the directory store and no other upload services, after the services that <paramref name="configureServices"/> registers, such
    /// as the configuration section of its options.
    /// </summary>
    private static Task<TestWebHost> StartWithDirectoryStoreAsync(
        Action<IServiceCollection> configureServices,
        Action<DirectoryUploadStoreOptions>? configure = null)
    {
        return TestWebHost.StartAsync(_ => { }, configureServices: services => {
            configureServices(services);
            services.AddDirectoryUploadStore(configure);
        });
    }
}
