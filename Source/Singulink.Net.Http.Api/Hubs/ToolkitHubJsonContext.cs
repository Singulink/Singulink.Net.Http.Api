using System.Text.Json;
using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api;

/// <summary>
/// The JSON type info of the arguments of the toolkit's own hub messages (see <see cref="ToolkitHubMessages"/>), which the hub payload options of the
/// service and the client get as their last type info resolver, so that the messages don't depend on the app's resolvers.
/// </summary>
[JsonSerializable(typeof(string))]
internal sealed partial class ToolkitHubJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Returns hub payload options that have the toolkit's type info as their last type info resolver: the specified options, or a copy of them if they are
    /// read-only. Options without a type info resolver are returned as they are, since their type info comes from reflection.
    /// </summary>
    public static JsonSerializerOptions AddTo(JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is null || options.TypeInfoResolverChain.Contains(Default))
            return options;

        if (options.IsReadOnly)
            options = new JsonSerializerOptions(options);

        options.TypeInfoResolverChain.Add(Default);
        return options;
    }
}
