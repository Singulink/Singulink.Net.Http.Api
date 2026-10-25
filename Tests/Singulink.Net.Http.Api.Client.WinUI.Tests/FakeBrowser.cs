using System.Net.Sockets;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Stands in for the system browser that a web authentication broker opens: records the launched address, follows its redirect to the address it opens,
/// and then requests the addresses the test supplies, like a browser following redirects to the app's loopback callback.
/// </summary>
internal sealed class FakeBrowser
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });

    private readonly Func<Uri, IEnumerable<string>> _getRequests;
    private readonly TaskCompletionSource<Uri> _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Uri> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HttpResponseMessage> _startResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<IReadOnlyList<(int Status, string Body)>> _responses = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeBrowser"/> class.
    /// </summary>
    /// <param name="getRequests">Gets the addresses to request, in order, from the opened address.</param>
    /// <param name="openIdleConnectionFirst">Whether to first open a connection that never sends a request, as browsers sometimes do.</param>
    public FakeBrowser(Func<Uri, IEnumerable<string>> getRequests, bool openIdleConnectionFirst = false)
    {
        _getRequests = getRequests;
        OpenIdleConnectionFirst = openIdleConnectionFirst;
    }

    public bool OpenIdleConnectionFirst { get; }

    /// <summary>
    /// Gets a value indicating whether the launcher reports success. Default is <see langword="true"/>.
    /// </summary>
    public bool LaunchSucceeds { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the browser opens the launched address. Default is <see langword="true"/>; <see langword="false"/> stands for a
    /// launcher that reports success without opening a browser.
    /// </summary>
    public bool Opens { get; init; } = true;

    /// <summary>
    /// Gets the address passed to the launcher.
    /// </summary>
    public Task<Uri> Launched => _launched.Task;

    /// <summary>
    /// Gets the address the browser opened: the target of the launched address's redirect, or the launched address if it doesn't redirect.
    /// </summary>
    public Task<Uri> Opened => _opened.Task;

    /// <summary>
    /// Gets the response to the launched address.
    /// </summary>
    public Task<HttpResponseMessage> StartResponse => _startResponse.Task;

    /// <summary>
    /// Gets the responses to the requested addresses.
    /// </summary>
    public Task<IReadOnlyList<(int Status, string Body)>> Responses => _responses.Task;

    public Task<bool> LaunchAsync(Uri uri)
    {
        _launched.SetResult(uri);

        if (LaunchSucceeds && Opens)
            _ = Task.Run(() => BrowseAsync(uri));

        return Task.FromResult(LaunchSucceeds);
    }

    private async Task BrowseAsync(Uri uri)
    {
        TcpClient? idleConnection = null;

        try
        {
            var startResponse = await HttpClient.GetAsync(uri);
            _startResponse.SetResult(startResponse);

            var opened = startResponse.Headers.Location ?? uri;
            _opened.SetResult(opened);

            var requests = _getRequests(opened).ToList();

            if (OpenIdleConnectionFirst && requests.Count > 0)
            {
                var target = new Uri(requests[0]);
                idleConnection = new TcpClient();
                await idleConnection.ConnectAsync(target.Host, target.Port);
            }

            var responses = new List<(int, string)>();

            foreach (string request in requests)
            {
                using var response = await HttpClient.GetAsync(request);
                responses.Add(((int)response.StatusCode, await response.Content.ReadAsStringAsync()));
            }

            _responses.SetResult(responses);
        }
        catch (Exception ex)
        {
            _startResponse.TrySetException(ex);
            _opened.TrySetException(ex);
            _responses.TrySetException(ex);
        }
        finally
        {
            idleConnection?.Dispose();
        }
    }
}
