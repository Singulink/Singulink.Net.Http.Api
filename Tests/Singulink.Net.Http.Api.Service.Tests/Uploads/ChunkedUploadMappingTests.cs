using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The mapping of upload endpoints and the registration and options of chunked uploads, which fail when the app starts if they cannot work.
/// </summary>
[PrefixTestClass]
public sealed class ChunkedUploadMappingTests
{
    private const string Pattern = "/folders/{folderId}/files";

    public sealed record Note(string Text);

    public sealed record OtherCommand(int Value);

    [TestMethod]
    public async Task Map_WithoutServices_Throws()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => TestWebHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync)));

        ex.Message.ShouldContain("AddChunkedUploads");
    }

    [TestMethod]
    public async Task Map_HandlerWithoutResult_Throws()
    {
        (await MapAndGetErrorAsync((UploadedFile file) => Task.CompletedTask)).ShouldContain("returns no result");
        (await MapAndGetErrorAsync((UploadedFile file) => { })).ShouldContain("returns no result");
    }

    [TestMethod]
    public async Task Map_HandlerWithValueTypeResult_Throws()
    {
        (await MapAndGetErrorAsync((UploadedFile file) => Task.FromResult(5))).ShouldContain("value type");
        (await MapAndGetErrorAsync((UploadedFile file) => ItemsAsync())).ShouldContain("value type");

        static async IAsyncEnumerable<int> ItemsAsync()
        {
            await Task.Yield();
            yield return 1;
        }
    }

    [TestMethod]
    public async Task Map_HandlerWithResultsResult_Throws()
    {
        (await MapAndGetErrorAsync((UploadedFile file) => Results.Ok(new Note("a")))).ShouldContain(nameof(IResult));
    }

    [TestMethod]
    public async Task Map_ValidatorWithResult_Throws()
    {
        string error = await MapAndGetErrorAsync((UploadRequest upload) => Task.FromResult(1), HandleAsync);

        error.ShouldContain("must return void, Task or ValueTask");
    }

    [TestMethod]
    public async Task Map_HandlerTakingUploadRequest_Throws()
    {
        (await MapAndGetErrorAsync((UploadRequest upload) => Task.FromResult(new Note("a")))).ShouldContain("only validators take");
    }

    [TestMethod]
    public async Task Map_ValidatorTakingUploadedFile_Throws()
    {
        (await MapAndGetErrorAsync((UploadedFile file) => { }, HandleAsync)).ShouldContain("only handlers take");
    }

    [TestMethod]
    public async Task Map_ValidatorAndHandlerWithDifferentCommands_Throws()
    {
        string error = await MapAndGetErrorAsync(
            (UploadRequest<Note> upload) => { },
            (UploadedFile<OtherCommand> file) => Task.FromResult(new Note("a")));

        error.ShouldContain("commands of different types");
    }

    [TestMethod]
    public async Task Start_WithoutDirectoryOrStore_Throws()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            options => options.Directory = null));

        ex.Message.ShouldContain(nameof(ChunkedUploadOptions.Directory));
    }

    [TestMethod]
    public async Task Start_WithoutSessionHandling_Throws()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => TestWebHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            configureServices: services => services.AddChunkedUploads<TestSessionToken>(o => o.Directory = UploadTestHost.CreateDirectoryPath())));

        ex.Message.ShouldContain("AddHttpSessionHandling");
    }

    [TestMethod]
    public async Task Start_WithMinChunkSizeOverMax_Throws()
    {
        var ex = await Should.ThrowAsync<OptionsValidationException>(() => UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            options => {
                options.MinChunkSize = 10 << 20;
                options.MaxChunkSize = 8 << 20;
            }));

        ex.Message.ShouldContain(nameof(ChunkedUploadOptions.MinChunkSize));
    }

    [TestMethod]
    public async Task Start_WithDefaultChunkSizeOutsideBounds_Throws()
    {
        var ex = await Should.ThrowAsync<OptionsValidationException>(() => UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            options => {
                options.MaxChunkSize = 8 << 20;
                options.DefaultChunkSize = 10 << 20;
            }));

        ex.Message.ShouldContain(nameof(ChunkedUploadOptions.DefaultChunkSize));
    }

    [TestMethod]
    public void Options_OutOfRange_Throw()
    {
        var options = new ChunkedUploadOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.DefaultChunkSize = (5 << 20) - 1);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MinChunkSize = (5 << 20) - 1);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxChunkSize = (64 << 20) + 1);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxChunkCount = 0);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxUploadsPerUser = 0);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxSize = -1);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxTotalSize = -1);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxTotalSizePerUser = -1);
        Should.Throw<ArgumentOutOfRangeException>(() => options.IdleExpiry = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.UnstartedExpiry = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.UnstartedExpiry = TimeSpan.FromDays(25));
        Should.Throw<ArgumentOutOfRangeException>(() => options.OutcomeRetention = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.CompletionPingInterval = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.IdleExpiry = TimeSpan.FromDays(24) + TimeSpan.FromTicks(1));
        Should.Throw<ArgumentOutOfRangeException>(() => options.OutcomeRetention = TimeSpan.MaxValue);
        Should.Throw<ArgumentOutOfRangeException>(() => options.CompletionPingInterval = TimeSpan.FromDays(25));
        Should.Throw<ArgumentException>(() => options.Directory = " ");
        Should.Throw<ArgumentOutOfRangeException>(() => new ChunkedUploadEndpointOptions().MaxSize = -1);
    }

    [TestMethod]
    public void Options_Defaults()
    {
        var options = new ChunkedUploadOptions();

        options.Directory.ShouldBeNull();
        options.MaxSize.ShouldBe(1L << 30);
        (options.DefaultChunkSize, options.MinChunkSize, options.MaxChunkSize).ShouldBe((5 << 20, 5 << 20, 64 << 20));
        options.MaxChunkCount.ShouldBe(10_000);
        options.MaxUploadsPerUser.ShouldBe(20);
        options.MaxTotalSize.ShouldBeNull();
        options.MaxTotalSizePerUser.ShouldBeNull();
        options.IdleExpiry.ShouldBe(TimeSpan.FromDays(1));
        options.UnstartedExpiry.ShouldBe(TimeSpan.FromMinutes(15));
        options.OutcomeRetention.ShouldBe(TimeSpan.FromHours(1));
        options.CompletionPingInterval.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task Group_TakesConventionsForAllEndpoints()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync).WithTags("uploads"));

        var endpoints = host.Web.Endpoints.OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText!.Contains("/uploads")).ToList();

        endpoints.Count.ShouldBe(5);
        endpoints.ShouldAllBe(e => e.Metadata.GetMetadata<ITagsMetadata>()!.Tags.Contains("uploads"));
    }

    [TestMethod]
    public async Task ChunkAndCompletion_HaveNoRequestTimeout()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync).WithRequestTimeout(TimeSpan.FromSeconds(1)));

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        endpoints.Where(e => e.Metadata.GetMetadata<DisableRequestTimeoutAttribute>() is not null).Select(e => e.DisplayName).Order().ShouldBe([
            "HTTP: POST /folders/{folderId}/files/uploads/{chunkedUploadId}/complete",
            "HTTP: PUT /folders/{folderId}/files/uploads/{chunkedUploadId}",
        ]);
    }

    [TestMethod]
    public async Task AddChunkedUploads_Twice_RegistersOnce()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            configureServices: services => services.AddChunkedUploads<TestSessionToken>());

        host.Services.GetServices<IUploadStore>().ShouldHaveSingleItem();
        (await host.UploadAsync(UploadTestHost.CreateFile(10))).Lines[^1].ShouldBe("""{"end":true}""");
    }

    private static Task<Note> HandleAsync(UploadedFile file) => Task.FromResult(new Note(file.FileName));

    private static async Task<string> MapAndGetErrorAsync(Delegate handler)
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, handler)));
        return ex.Message;
    }

    private static async Task<string> MapAndGetErrorAsync(Delegate validator, Delegate handler)
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, validator, handler)));

        return ex.Message;
    }
}
