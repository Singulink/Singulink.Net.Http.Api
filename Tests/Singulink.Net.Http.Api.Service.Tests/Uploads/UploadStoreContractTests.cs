using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.Service.Testing;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The guarantees of upload stores (see <see cref="UploadStoreContract"/>), for the directory store and a store that keeps uploads in memory.
/// </summary>
[PrefixTestClass]
public sealed class UploadStoreContractTests
{
    [TestMethod]
    public async Task DirectoryStore_KeepsTheGuarantees()
    {
        // The checks wait for a claim's lease to lapse, so the store's clock runs.
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload("/files", HandleAsync), timeProvider: TimeProvider.System);
        var store = host.Services.GetRequiredService<IUploadStore>();

        await UploadStoreContract.CheckAsync(store);
        await UploadStoreContract.CheckOutcomeRemovalAsync(store);
    }

    [TestMethod]
    public async Task MemoryStore_KeepsTheGuarantees()
    {
        var store = new MemoryUploadStore(TimeProvider.System);

        await UploadStoreContract.CheckAsync(store);
        await UploadStoreContract.CheckOutcomeRemovalAsync(store);
    }

    private static Task<Note> HandleAsync(UploadedFile file) => Task.FromResult(new Note(file.FileName));
}
