using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// An in-process web application with session handling and chunked uploads, kept in a temporary directory unless a store or a directory of a test's own
/// is used, and the requests of the upload protocol for signed-in users.
/// </summary>
public sealed class UploadTestHost : IAsyncDisposable
{
    public const string Path = "/folders/5/files";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly bool _ownsDirectory;

    private UploadTestHost(TestWebHost web, string directory, bool ownsDirectory, TestClock clock)
    {
        Web = web;
        Directory = directory;
        _ownsDirectory = ownsDirectory;
        Clock = clock;
    }

    public TestWebHost Web { get; }

    public HttpClient Client => Web.Client;

    /// <summary>
    /// Gets the directory of the directory store.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Gets the clock of the host, whose time a test can advance. Its timers run in real time.
    /// </summary>
    public TestClock Clock { get; }

    public IServiceProvider Services => Web.App.Services;

    public static string CreateDirectoryPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "httpapi-uploads-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Starts a host with the specified endpoints mapped.
    /// </summary>
    /// <param name="map">Maps the endpoints, such as with <c>MapChunkedUpload</c>.</param>
    /// <param name="configure">Configures the options of chunked uploads after the directory is set.</param>
    /// <param name="directory">The directory of the directory store, such as one that another host uses too, or <see langword="null"/> for a new temporary
    /// one that is deleted with the host.</param>
    /// <param name="dataProtection">The data protection of session cookies, such as one that another host uses too.</param>
    /// <param name="clock">The clock of the host, such as one that another host uses too.</param>
    /// <param name="development">Whether the host runs in the Development environment.</param>
    /// <param name="configureServices">Registers more services, such as an upload store.</param>
    /// <param name="timeProvider">The time provider of the service instead of the clock, such as a <see cref="ManualClock"/>, whose time the clock then
    /// starts at.</param>
    /// <param name="kestrel">Whether the host runs on Kestrel on a port of the loopback address rather than on the test server.</param>
    /// <param name="backgroundCleanup">Whether the cleanup runs in the background, as on a server: at the start, and each time its interval passes on the time
    /// provider. Without it, a cleanup only runs when a test runs one, so that nothing changes or lists the store between the steps of a test.</param>
    /// <param name="shortLivedStore">The short-lived store, which keeps the counts of open uploads, such as one that another host uses too, as servers that
    /// share uploads must, or <see langword="null"/> for a memory store of the host's own. The directory store is shared (see
    /// <see cref="DirectoryUploadStoreOptions.SharingMode"/>) if the short-lived store is, such as a <see cref="DeclaredSharedShortLivedStore"/>. Otherwise it
    /// is process-local. A store that <paramref name="configureServices"/> registers takes the directory store's place, and a call of
    /// <see cref="ServiceCollectionExtensions.AddDirectoryUploadStore"/> there configures the directory store after these values.</param>
    public static async Task<UploadTestHost> StartAsync(
        Action<WebApplication> map,
        Action<ChunkedUploadOptions>? configure = null,
        string? directory = null,
        IDataProtectionProvider? dataProtection = null,
        TestClock? clock = null,
        bool development = false,
        Action<IServiceCollection>? configureServices = null,
        TimeProvider? timeProvider = null,
        bool kestrel = false,
        bool backgroundCleanup = false,
        IShortLivedStore? shortLivedStore = null)
    {
        bool ownsDirectory = directory is null;
        directory ??= CreateDirectoryPath();
        clock ??= timeProvider is null ? new TestClock() : new TestClock(timeProvider.GetUtcNow());

        var sessions = new InMemorySessionStore();

        foreach (int userId in new[] { 1, 2 })
        {
            sessions.UserStamps[userId] = 1;
            sessions.Sessions[userId] = new TestSessionData {
                Id = userId,
                UserId = userId,
                Device = "TestClient/1.0",
                RefreshedUtc = clock.GetUtcNow().UtcDateTime,
                ExpiresUtc = clock.GetUtcNow().UtcDateTime.AddDays(30),
                IsPersistent = true,
                CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
        }

        var web = await TestWebHost.StartAsync(
            map,
            development: development,
            configureServices: services => {
                if (dataProtection is not null)
                    services.AddSingleton(dataProtection);

                services.AddSingleton(timeProvider ?? clock);

                if (shortLivedStore is not null)
                    services.AddSingleton(shortLivedStore);

                services.AddApiExceptionHandler();
                services.AddAllowedOrigins("example.com");
                services.AddSingleton(sessions);
                services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
                services.AddChunkedUploads<TestSessionToken>(options => configure?.Invoke(options));

                var sharingMode = shortLivedStore is { SharingMode: StoreSharingMode.Shared } ? StoreSharingMode.Shared : StoreSharingMode.ProcessLocal;
                services.AddDirectoryUploadStore(options => {
                    options.Directory = TestPaths.ToDirectory(directory);
                    options.SharingMode = sharingMode;
                });

                configureServices?.Invoke(services);

                if (!backgroundCleanup)
                    services.Remove(services.Single(s => s.ImplementationType == typeof(ChunkedUploadCleanup)));
            },
            kestrel: kestrel);

        return new UploadTestHost(web, directory, ownsDirectory, clock);
    }

    public static byte[] CreateFile(int length, int seed = 1)
    {
        byte[] file = new byte[length];
        new Random(seed).NextBytes(file);
        return file;
    }

    public static string Hash(byte[] file) => Convert.ToHexString(SHA256.HashData(file));

    public static async Task<string> HashAsync(UploadedFile file, CancellationToken cancellationToken = default)
    {
        await using var stream = file.OpenReadStream();
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>
    /// Asserts that a response is an error in the toolkit's format with the specified status and error code, or without an error code.
    /// </summary>
    public static async Task ShouldBeErrorAsync(HttpResponseMessage response, HttpStatusCode status, string? errorCode)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/plain", body);

        if (errorCode is null)
            body.ShouldNotStartWith("[", Case.Sensitive, body);
        else
            body.ShouldStartWith($"[{errorCode}] ", Case.Sensitive, body);
    }

    /// <summary>
    /// Gets the lines of a completion's stream without its pings, whose number depends on timing.
    /// </summary>
    public static List<string> WithoutPings(List<string> lines) => [.. lines.Where(l => l != """{"ping":true}""")];

    public static async Task<List<string>> ReadLinesAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        return [.. body.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// Creates a request of a signed-in user, with the user's session cookie and user ID precondition.
    /// </summary>
    public HttpRequestMessage CreateRequest(HttpMethod method, string path, int userId = 1)
    {
        var request = new HttpRequestMessage(method, path + (path.Contains('?') ? "&" : "?") + $"if-userId={userId}");
        request.Headers.Add("User-Agent", "TestClient/1.0");

        var refreshedUtc = Clock.GetUtcNow().UtcDateTime;
        var cookie = new TestSessionCookie(new TestSessionToken(userId, userId, 1), refreshedUtc, TimeSpan.FromDays(30), 0, true);
        request.Headers.Add("Cookie", $"{TestSessionCookies.CookieName}={TestSessionCookies.Protect(Services, cookie)}");

        return request;
    }

    /// <summary>
    /// Creates the request of a direct upload of the user: the file as the body, and the values of its creation in the creation header. The length is that of
    /// the content, unless it is specified.
    /// </summary>
    public HttpRequestMessage CreateDirectRequest(
        HttpContent content,
        string fileName = "photo.jpg",
        string? key = null,
        string path = Path,
        int userId = 1,
        string? query = null,
        string? contentType = null,
        object? command = null,
        long? length = null,
        Guid? id = null)
    {
        var request = CreateRequest(HttpMethod.Post, $"{path}/uploads/direct{(query is null ? null : "?" + query)}", userId);
        request.Content = content;

        length ??= content.Headers.ContentLength ?? throw new ArgumentException("The content has no length.", nameof(content));
        var creation = new { id, fileName, length, contentType, command, idempotencyKey = key };
        request.Headers.TryAddWithoutValidation("Upload-Creation", EncodeHeader(JsonSerializer.SerializeToUtf8Bytes(creation, JsonOptions)));

        return request;
    }

    /// <summary>
    /// Encodes the value of the creation header of a direct upload as base64url without padding.
    /// </summary>
    public static string EncodeHeader(byte[] bytes) => Base64Url.EncodeToString(bytes);

    public Task<HttpResponseMessage> SendCreateAsync(object body, string path = Path, int userId = 1, string? query = null)
    {
        var request = CreateRequest(HttpMethod.Post, $"{path}/uploads{(query is null ? null : "?" + query)}", userId);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        return Client.SendAsync(request);
    }

    public async Task<UploadSession> CreateUploadAsync(
        string fileName,
        long length,
        int? chunkSize = null,
        object? command = null,
        string? contentType = null,
        string path = Path,
        int userId = 1,
        string? query = null)
    {
        using var response = await SendCreateAsync(new { fileName, length, contentType, chunkSize, command }, path, userId, query);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<UploadSession>(JsonOptions)).ShouldNotBeNull();
    }

    public Task<HttpResponseMessage> PutChunkAsync(UploadSession session, byte[] file, int index, string path = Path, int userId = 1)
    {
        long offset = (long)index * session.ChunkSize;
        int length = (int)Math.Min(session.ChunkSize, file.Length - offset);

        var request = CreateRequest(HttpMethod.Put, $"{path}/uploads/{session.Id}", userId);
        request.Content = new ByteArrayContent(file, (int)offset, length);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, file.Length);

        return Client.SendAsync(request);
    }

    public async Task PutChunksAsync(UploadSession session, byte[] file, string path = Path, int userId = 1)
    {
        int chunkCount = (int)((file.Length + session.ChunkSize - 1) / session.ChunkSize);

        for (int index = 0; index < chunkCount; index++)
        {
            using var response = await PutChunkAsync(session, file, index, path, userId);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        }
    }

    public Task<HttpResponseMessage> CompleteAsync(UploadSession session, string path = Path, int userId = 1, string? query = null)
    {
        var request = CreateRequest(HttpMethod.Post, $"{path}/uploads/{session.Id}/complete{(query is null ? null : "?" + query)}", userId);
        return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    public async Task<List<string>> CompleteAndReadAsync(UploadSession session, string path = Path, int userId = 1, string? query = null)
    {
        using var response = await CompleteAsync(session, path, userId, query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/jsonl");

        return await ReadLinesAsync(response);
    }

    public Task<HttpResponseMessage> GetStatusResponseAsync(UploadSession session, string path = Path, int userId = 1)
    {
        return Client.SendAsync(CreateRequest(HttpMethod.Get, $"{path}/uploads/{session.Id}", userId));
    }

    public async Task<UploadStatus> GetStatusAsync(UploadSession session, string path = Path, int userId = 1)
    {
        using var response = await GetStatusResponseAsync(session, path, userId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<UploadStatus>(JsonOptions)).ShouldNotBeNull();
    }

    public Task<HttpResponseMessage> AbortAsync(UploadSession session, string path = Path, int userId = 1)
    {
        return Client.SendAsync(CreateRequest(HttpMethod.Delete, $"{path}/uploads/{session.Id}", userId));
    }

    /// <summary>
    /// Creates an upload of the file, sends its chunks and completes it, with the same query string for the creation and the completion.
    /// </summary>
    public async Task<(UploadSession Session, List<string> Lines)> UploadAsync(
        byte[] file, string fileName = "file.bin", int? chunkSize = null, object? command = null, string? contentType = null, string? query = null)
    {
        var session = await CreateUploadAsync(fileName, file.Length, chunkSize, command, contentType, query: query);
        await PutChunksAsync(session, file);

        return (session, await CompleteAndReadAsync(session, query: query));
    }

    /// <summary>
    /// Gets the directory of an upload in the directory store.
    /// </summary>
    public string GetUploadDirectory(UploadSession session) => System.IO.Directory.GetDirectories(Directory, session.Id, SearchOption.AllDirectories).Single();

    /// <summary>
    /// Gets the file of the outcome of a finished upload in the directory store, in the user's outcomes.
    /// </summary>
    public string GetOutcomeFile(UploadSession session) => System.IO.Directory.GetFiles(Directory, session.Id + ".*", SearchOption.AllDirectories).Single();

    /// <summary>
    /// Counts the uploads in the directory store, each once: the open ones, by their records, the finished ones, by their outcomes, and the ones being
    /// created, by the records in the directories of their creations, <c>{uploadId}.{random}.new</c>. For a moment, a finishing upload has its record next
    /// to its outcome, a deleted one its record next to the marker of its deletion, and an upload that two requests create its record in the directory of
    /// the creation that lost too, until these directories are removed.
    /// </summary>
    public int CountUploads()
    {
        if (!System.IO.Directory.Exists(Directory))
            return 0;

        // On Windows, a directory that is being removed cannot be listed for a moment.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        // A deletion creates its marker before it removes the record, and removes the marker with the directory, so a record that is still there once the
        // marker isn't is of an upload that isn't deleted.
        var records = System.IO.Directory.GetFiles(Directory, "upload.json", options)
            .Select(f => System.IO.Path.GetDirectoryName(f)!)
            .Where(d => !File.Exists(System.IO.Path.Combine(d, "deleted")) && File.Exists(System.IO.Path.Combine(d, "upload.json")))
            .Select(d => GetUploadKey(System.IO.Path.GetDirectoryName(d)!, System.IO.Path.GetFileName(d)));

        // The outcomes, {uploadId}.{ticks}.json, are in the user's directory, as the directories of the user's uploads are.
        var outcomes = System.IO.Directory.GetDirectories(Directory, "outcomes", options)
            .SelectMany(d => System.IO.Directory.GetFiles(d, "*.json"))
            .Select(f => GetUploadKey(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(f))!, System.IO.Path.GetFileName(f)));

        return records.Concat(outcomes).Distinct(StringComparer.Ordinal).Count();

        // IDs have no dots, so the name of an upload's directory, of a creation's directory or of an outcome starts with the ID up to its first dot.
        static string GetUploadKey(string userDirectory, string name) => System.IO.Path.Combine(userDirectory, name.Split('.')[0]);
    }

    public async ValueTask DisposeAsync()
    {
        await Web.DisposeAsync();

        if (_ownsDirectory)
            DeleteDirectory(Directory);
    }

    /// <summary>
    /// Deletes a temporary directory of a test if it can, again a moment later while a file in it is open in another process, such as a virus scanner, which
    /// leaves the file being deleted, and the directory with it.
    /// </summary>
    public static void DeleteDirectory(string directory)
    {
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
