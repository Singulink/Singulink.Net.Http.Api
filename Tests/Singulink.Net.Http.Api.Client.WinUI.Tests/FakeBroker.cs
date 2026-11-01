using Singulink.Net.Http.Api.Client.Passkeys;
using Windows.Security.Authentication.Web;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Stands in for Uno Platform's web authentication broker: records the requests and returns to the callback address with the response query that the test
/// builds from the request's query, or returns <see cref="Result"/> if there is no response.
/// </summary>
internal sealed class FakeBroker(Func<IReadOnlyDictionary<string, string>, string>? getResponseQuery)
{
    public List<(Uri RequestUri, Uri CallbackUri)> Requests { get; } = [];

    public WebAuthenticationResult Result { get; init; } = new(null, 0, WebAuthenticationStatus.UserCancel);

    /// <summary>
    /// Gets a value indicating whether the broker reports a closed browser tab as a cancellation, as Uno Platform's Android broker does.
    /// </summary>
    public bool ThrowsCancellation { get; init; }

    /// <summary>
    /// Gets the exception that the broker throws, if any.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Gets a value indicating whether the broker waits until the caller's token is cancelled.
    /// </summary>
    public bool WaitForCancellation { get; init; }

    public async Task<WebAuthenticationResult> AuthenticateAsync(Uri requestUri, Uri callbackUri, CancellationToken cancellationToken)
    {
        Requests.Add((requestUri, callbackUri));

        if (WaitForCancellation)
            await Task.Delay(Timeout.Infinite, cancellationToken);

        await Task.Yield();

        if (Exception is not null)
            throw Exception;

        if (ThrowsCancellation)
            throw new OperationCanceledException();

        if (getResponseQuery is null)
            return Result;

        var query = PasskeyPageProtocol.ParseQuery(requestUri.OriginalString);
        return new WebAuthenticationResult($"{query["redirect"]}?{getResponseQuery(query)}", 0, WebAuthenticationStatus.Success);
    }
}
