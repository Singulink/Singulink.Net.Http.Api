using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Creates the options of the memory stores, which start with <see cref="MemoryShortLivedStoreOptions.DefaultKeyedMaxSize"/> as their size for the keys of
/// <see cref="ShortLivedStoreKeys"/>, before the app's configuration of each name runs, in whatever order it was registered.
/// </summary>
internal sealed class MemoryShortLivedStoreOptionsFactory(
    IEnumerable<IConfigureOptions<MemoryShortLivedStoreOptions>> setups,
    IEnumerable<IPostConfigureOptions<MemoryShortLivedStoreOptions>> postConfigures,
    IEnumerable<IValidateOptions<MemoryShortLivedStoreOptions>> validations) :
    OptionsFactory<MemoryShortLivedStoreOptions>(setups, postConfigures, validations)
{
    protected override MemoryShortLivedStoreOptions CreateInstance(string name)
    {
        return ShortLivedStoreKeys.IsToolkitKey(name) ? new() { MaxSize = MemoryShortLivedStoreOptions.DefaultKeyedMaxSize } : new();
    }
}
