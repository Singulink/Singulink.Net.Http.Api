using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace Singulink.Net.Http.Api.Service.AotTestApp;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// A hub that requires a session, with a method that sends a message back to the caller, a method that fails, and a stream that fails after its items.
/// </summary>
internal sealed class AppHub : Hub
{
    private const string UserIdKey = "UserId";

    public override async Task OnConnectedAsync()
    {
        var token = await Context.GetRequiredSessionTokenAsync<AppSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition);
        Context.Items[UserIdKey] = token.UserId;
    }

    public async Task<string> Echo(string text)
    {
        await Clients.Caller.SendAsync(AppHubContract.Echoed, text);
        return $"{Context.Items[UserIdKey]}: {text}";
    }

    public string Fail() => throw new ConflictApiException("Already exists.") { ErrorCode = "already-exists" };

    public async IAsyncEnumerable<string> CountThenFail(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 1; i <= count; i++)
        {
            await Task.Yield();
            yield return i.ToString(CultureInfo.InvariantCulture);
        }

        throw new ConflictApiException("Stream failed.") { ErrorCode = "stream-failed" };
    }
}

/// <summary>
/// The hub's contract, which the self-check's client uses by name.
/// </summary>
internal static class AppHubContract
{
    public static HubMethod<string, string> Echo { get; } = new();

    public static HubMessage<string> Echoed { get; } = new();

    public static HubMethod<string> Fail { get; } = new();

    public static HubStream<int, string> CountThenFail { get; } = new();
}
