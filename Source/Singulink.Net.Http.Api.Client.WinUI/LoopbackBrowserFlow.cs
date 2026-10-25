#if !__WASM__ && !__IOS__ && !__ANDROID__

using System.Net;
using System.Net.Sockets;
using System.Text;
using Singulink.Net.Http.Api.Client.Passkeys;
using Windows.System;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Opens a request in the default browser and receives the result on a loopback address, the standard way for native apps to get a result back from the
/// browser (RFC 8252). The browser opens a short start address on the loopback listener, which redirects to the request, so that a browser that never
/// opened is noticed quickly and long requests are never passed to the system launcher.
/// </summary>
internal sealed class LoopbackBrowserFlow
{
    public const string StartPath = "/start";

    private const string CallbackPath = "/callback";
    private const int MaxRequestLineLength = 64 * 1024;

    private readonly Func<Uri, Task<bool>> _launchUriAsync;
    private TcpListener? _listener;

    public LoopbackBrowserFlow() : this(uri => Launcher.LaunchUriAsync(uri).AsTask())
    {
    }

    public LoopbackBrowserFlow(Func<Uri, Task<bool>> launchUriAsync)
    {
        _launchUriAsync = launchUriAsync;
    }

    /// <summary>
    /// Gets or sets the message shown in the browser after it returns to the app.
    /// </summary>
    public string CompletedMessage { get; set; } = PasskeyAuthenticatorOptions.DefaultCompletedMessage;

    /// <summary>
    /// Gets or sets how long to wait for the browser to return. Default is <see langword="null"/>, which uses Uno's
    /// <c>WinRTFeatureConfiguration.WebAuthenticationBroker.AuthenticationTimeout</c> where Uno is used, and 5 minutes elsewhere.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets or sets how long to wait for the browser to open the start address.
    /// </summary>
    public TimeSpan StartTimeout { get; set; } = PasskeyAuthenticatorOptions.DefaultStartTimeout;

    /// <summary>
    /// Starts listening on a free loopback port and returns the callback address on it, for use by the next <see cref="RunAsync"/> call.
    /// </summary>
    public Uri CreateCallbackUri()
    {
        // The address has to be known before the request is built, so the listener starts here.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        Interlocked.Exchange(ref _listener, listener)?.Stop();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new Uri($"http://127.0.0.1:{port}{CallbackPath}");
    }

    /// <summary>
    /// Opens <paramref name="requestUri"/> in the default browser and waits for the browser to return to <paramref name="callbackUri"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="callbackUri"/> is not a loopback HTTP address, or its path is the start path.</exception>
    public async Task<LoopbackBrowserResult> RunAsync(Uri requestUri, Uri callbackUri, CancellationToken cancellationToken)
    {
        var listener = TakeListener(callbackUri);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(Timeout ?? GetDefaultTimeout());

        var state = new FlowState(requestUri, callbackUri.AbsolutePath);
        string authority = callbackUri.GetLeftPart(UriPartial.Authority);

        try
        {
            _ = AcceptConnectionsAsync(listener, state, lifetime.Token);

            using (var start = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                start.CancelAfter(StartTimeout);

                try
                {
                    if (!await _launchUriAsync(new Uri(authority + StartPath)).WaitAsync(start.Token).ConfigureAwait(false))
                        return new(LoopbackBrowserStatus.LaunchFailed);

                    await state.Started.Task.WaitAsync(start.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    return new(LoopbackBrowserStatus.NotOpened);
                }
            }

            string target = await state.Callback.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
            return new(LoopbackBrowserStatus.Success, authority + target);
        }
        catch (OperationCanceledException)
        {
            return new(cancellationToken.IsCancellationRequested ? LoopbackBrowserStatus.Cancelled : LoopbackBrowserStatus.TimedOut);
        }
        finally
        {
            // Also ends the handlers of connections that are still open, such as connections a browser opened speculatively.
            lifetime.Cancel();
            listener.Stop();
        }
    }

    private static TimeSpan GetDefaultTimeout() =>
#if HAS_UNO && !WINDOWS
        global::Uno.WinRTFeatureConfiguration.WebAuthenticationBroker.AuthenticationTimeout;
#else
        TimeSpan.FromMinutes(5);
#endif

    private TcpListener TakeListener(Uri callbackUri)
    {
        if (!callbackUri.IsAbsoluteUri || callbackUri.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(callbackUri.IdnHost, out var address) ||
            !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("The callback URI must be a loopback HTTP address with an IP address host.", nameof(callbackUri));
        }

        if (callbackUri.AbsolutePath == StartPath)
            throw new ArgumentException($"The callback URI's path can't be '{StartPath}', which opens the request.", nameof(callbackUri));

        var listener = Interlocked.Exchange(ref _listener, null);

        if (listener is not null && ((IPEndPoint)listener.LocalEndpoint).Port == callbackUri.Port && address.Equals(IPAddress.Loopback))
            return listener;

        // The callback wasn't created by CreateCallbackUri, so listen where it points.
        listener?.Stop();
        listener = new TcpListener(address, callbackUri.Port);
        listener.Start();

        return listener;
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, FlowState state, CancellationToken ct)
    {
        try
        {
            while (!state.Callback.Task.IsCompleted)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);

                // Connections are handled concurrently: browsers can open connections that never send a request.
                _ = HandleConnectionAsync(client, state, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The flow ended and the listener was stopped.
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, FlowState state, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                string? target = await ReadRequestTargetAsync(stream, ct).ConfigureAwait(false);

                if (target is null)
                {
                    await WriteResponseAsync(stream, "400 Bad Request", "Bad request.", ct).ConfigureAwait(false);
                }
                else if (GetPath(target).SequenceEqual(state.CallbackPath))
                {
                    // Respond before completing, so the page is sent before the listener stops.
                    await WriteResponseAsync(stream, "200 OK", CompletedMessage, ct).ConfigureAwait(false);
                    state.Started.TrySetResult();
                    state.Callback.TrySetResult(target);
                }
                else if (GetPath(target).SequenceEqual(StartPath))
                {
                    await WriteRedirectAsync(stream, state.RequestUri, ct).ConfigureAwait(false);
                    state.Started.TrySetResult();
                }
                else
                {
                    // Browsers also ask for /favicon.ico and the like; only the callback completes the flow.
                    await WriteResponseAsync(stream, "404 Not Found", "Not found.", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection closed or the flow ended.
            }
        }

        static ReadOnlySpan<char> GetPath(string target) => target.AsSpan(0, target.AsSpan().IndexOfAny('?', '#') is int end and >= 0 ? end : target.Length);
    }

    /// <summary>
    /// Reads the request line (e.g. <c>GET /callback?state=... HTTP/1.1</c>) and returns its target, or <see langword="null"/> if it is not a GET
    /// request or is too long.
    /// </summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxRequestLineLength];
        int length = 0;
        int lineEnd = -1;

        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);

            if (read is 0)
                break;

            length += read;
            lineEnd = buffer.AsSpan(0, length).IndexOf("\r\n"u8);

            if (lineEnd >= 0)
                break;
        }

        if (lineEnd < 0)
            return null;

        string[] parts = Encoding.ASCII.GetString(buffer, 0, lineEnd).Split(' ');
        return parts is ["GET", ['/', ..] target, _] ? target : null;
    }

    private static async Task WriteRedirectAsync(NetworkStream stream, Uri location, CancellationToken ct)
    {
        string headers =
            $"HTTP/1.1 302 Found\r\nLocation: {location.AbsoluteUri}\r\nContent-Length: 0\r\nReferrer-Policy: no-referrer\r\nCache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(headers), ct).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string message, CancellationToken ct)
    {
        string encodedMessage = WebUtility.HtmlEncode(message);
        string body =
            $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>{encodedMessage}</title>" +
            "<style>body{font:16px system-ui,sans-serif;text-align:center;margin-top:20vh}</style></head>" +
            $"<body><p>{encodedMessage}</p></body></html>";

        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

        string headers =
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nReferrer-Policy: no-referrer\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
    }

    private sealed class FlowState(Uri requestUri, string callbackPath)
    {
        public Uri RequestUri { get; } = requestUri;

        public string CallbackPath { get; } = callbackPath;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Callback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Specifies how a <see cref="LoopbackBrowserFlow"/> ended.
/// </summary>
internal enum LoopbackBrowserStatus
{
    /// <summary>
    /// The browser returned to the callback address.
    /// </summary>
    Success,

    /// <summary>
    /// The system launcher reported that it could not open the browser.
    /// </summary>
    LaunchFailed,

    /// <summary>
    /// The browser did not open the start address in time.
    /// </summary>
    NotOpened,

    /// <summary>
    /// The browser did not return in time.
    /// </summary>
    TimedOut,

    /// <summary>
    /// The caller cancelled the flow.
    /// </summary>
    Cancelled,
}

/// <summary>
/// The result of a <see cref="LoopbackBrowserFlow"/>: how it ended, and on success the full callback address the browser returned to.
/// </summary>
internal readonly record struct LoopbackBrowserResult(LoopbackBrowserStatus Status, string? CallbackUri = null);

#endif
