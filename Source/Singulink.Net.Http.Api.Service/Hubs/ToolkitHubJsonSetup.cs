using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Adds the type info of the arguments of the toolkit's own hub messages to the hub payload options, after the app's type info resolvers.
/// </summary>
internal sealed class ToolkitHubJsonSetup : IPostConfigureOptions<JsonHubProtocolOptions>
{
    public void PostConfigure(string? name, JsonHubProtocolOptions options)
    {
        options.PayloadSerializerOptions = ToolkitHubJsonContext.AddTo(options.PayloadSerializerOptions);
    }
}
