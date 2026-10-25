using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Chunked uploads with an upload store of the app's own instead of the directory store.
/// </summary>
[PrefixTestClass]
public sealed class UploadStoreTests
{
    private const string Pattern = "/folders/{folderId}/files";
    private const int MiB = 1 << 20;

    public sealed record Note(string Text);

    [TestMethod]
    public async Task OwnStore_IsUsedInsteadOfDirectory()
    {
        string destination = Path.Combine(Path.GetTempPath(), "httpapi-moved-" + Guid.NewGuid().ToString("N"));
        var store = new MemoryUploadStore(TimeProvider.System);
        byte[] file = UploadTestHost.CreateFile((5 * MiB) + 10);

        try
        {
            await using var host = await StartAsync(store, async (UploadedFile uploaded) => {
                string hash = await UploadTestHost.HashAsync(uploaded);
                await uploaded.MoveToAsync(destination);

                return new Note($"{uploaded.FilePath is null} {hash}");
            });

            var (_, lines) = await host.UploadAsync(file);

            lines[0].ShouldBe($$$"""{"item":{"text":"True {{{UploadTestHost.Hash(file)}}}"}}""");
            UploadTestHost.Hash(await File.ReadAllBytesAsync(destination)).ShouldBe(UploadTestHost.Hash(file));
            Directory.Exists(host.Directory).ShouldBeFalse();
            store.Count.ShouldBe(1);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [TestMethod]
    public async Task StoreMinChunkSize_RaisesChunkSizes()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { MinChunkSize = 8 * MiB };
        await using var host = await StartAsync(store, HandleAsync);

        (await host.CreateUploadAsync("a", 10)).ChunkSize.ShouldBe(8 * MiB);
        (await host.CreateUploadAsync("a", 10, 6 * MiB)).ChunkSize.ShouldBe(8 * MiB);
        (await host.CreateUploadAsync("a", 10, 9 * MiB)).ChunkSize.ShouldBe(9 * MiB);
    }

    [TestMethod]
    public async Task StoreMaxChunkSizeAndCount_LimitChunkSizes()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { MaxChunkSize = 6 * MiB, MaxChunkCount = 2 };
        await using var host = await StartAsync(store, HandleAsync);

        (await host.CreateUploadAsync("a", 10, 64 * MiB)).ChunkSize.ShouldBe(6 * MiB);
        (await host.CreateUploadAsync("a", 11 * MiB)).ChunkSize.ShouldBe((int)(5.5 * MiB));

        using var response = await host.SendCreateAsync(new { fileName = "a", length = (12L * MiB) + 1 });
        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);
    }

    [TestMethod]
    public async Task ChunkForWhichTheStoreHasNoRoom_IsStorageFull()
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        await using var host = await StartAsync(store, HandleAsync);
        var session = await host.CreateUploadAsync("a", 10);

        store.IsFull = true;
        using var response = await host.PutChunkAsync(session, UploadTestHost.CreateFile(10), 0);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StorageFull);
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromMinutes(1));
        (await host.GetStatusAsync(session)).MissingChunks.ShouldBe([new ChunkRange(0, 0)]);
    }

    [TestMethod]
    public async Task StoreChunkSizesOutsideOptions_FailStartup()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { MinChunkSize = 32 * MiB };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            options => options.MaxChunkSize = 16 * MiB,
            configureServices: services => services.AddSingleton<IUploadStore>(store)));

        ex.Message.ShouldContain("have no size in common");
    }

    private static Task<Note> HandleAsync(UploadedFile file) => Task.FromResult(new Note(file.FileName));

    private static Task<UploadTestHost> StartAsync(MemoryUploadStore store, Delegate handler)
    {
        return UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, handler),
            options => options.Directory = null,
            configureServices: services => services.AddSingleton<IUploadStore>(store));
    }
}
