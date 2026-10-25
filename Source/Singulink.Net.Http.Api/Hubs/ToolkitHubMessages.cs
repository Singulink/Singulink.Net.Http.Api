namespace Singulink.Net.Http.Api;

/// <summary>
/// The names of the messages that the toolkit's service sends to hub connections, which the toolkit's client handles itself.
/// </summary>
internal static class ToolkitHubMessages
{
    /// <summary>
    /// The message that the service sends to a connection whose session ended before it closes the connection. Its argument is the error that ended the
    /// session, formatted as a hub error (see <see cref="ResponseExceptionInfo.ToHubErrorString"/>).
    /// </summary>
    public const string SessionEnded = "Singulink.Net.Http.Api.SessionEnded";

    /// <summary>
    /// The message, without arguments, that the service sends to a connection once the hub accepted it: once the hub's <c>OnConnectedAsync</c> succeeded,
    /// which SignalR runs after the handshake.
    /// </summary>
    public const string Connected = "Singulink.Net.Http.Api.Connected";
}
