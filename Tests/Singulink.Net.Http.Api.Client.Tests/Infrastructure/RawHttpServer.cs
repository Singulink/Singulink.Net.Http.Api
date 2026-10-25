using System.Net.Sockets;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// An HTTP/1.1 server on a loopback address that records each request's head and writes a raw response, one request per connection. Tests use it where the
/// client's own HTTP handler matters, which a <c>FakeHttpMessageHandler</c> replaces.
/// </summary>
public sealed class RawHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, Stream, Task> _respond;
    private readonly List<string> _requests = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="RawHttpServer"/> class that answers every request with the response text that the callback returns
    /// for the request's head.
    /// </summary>
    public RawHttpServer(IPAddress address, Func<string, string> respond)
        : this(address, (head, stream) => WriteAsync(stream, respond(head)))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RawHttpServer"/> class that answers every request by writing to the connection's stream with the
    /// callback. The connection is closed when the callback's task completes.
    /// </summary>
    public RawHttpServer(IPAddress address, Func<string, Stream, Task> respond)
    {
        _respond = respond;
        _listener = new TcpListener(address, 0);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Gets the address of the server, such as <c>http://127.0.0.1:12345/</c>.
    /// </summary>
    public Uri BaseAddress
    {
        get {
            var endPoint = (IPEndPoint)_listener.LocalEndpoint;
            return new Uri($"http://{endPoint.Address}:{endPoint.Port}/");
        }
    }

    /// <summary>
    /// Gets the heads of the requests received so far.
    /// </summary>
    public IReadOnlyList<string> Requests
    {
        get {
            lock (_requests)
                return [.. _requests];
        }
    }

    /// <summary>
    /// Writes the text to the stream as ASCII.
    /// </summary>
    public static async Task WriteAsync(Stream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, 0, bytes.Length);
        await stream.FlushAsync();
    }

    public void Dispose() => _listener.Stop();

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                string head = await ReadHeadAsync(stream);

                lock (_requests)
                    _requests.Add(head);

                await _respond(head, stream);
            }
            catch (IOException)
            {
                // The client closed the connection.
            }
        }
    }

    private static async Task<string> ReadHeadAsync(Stream stream)
    {
        byte[] buffer = new byte[8192];
        var head = new StringBuilder();

        while (!head.ToString().Contains("\r\n\r\n"))
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length);

            if (read is 0)
                break;

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return head.ToString();
    }
}
