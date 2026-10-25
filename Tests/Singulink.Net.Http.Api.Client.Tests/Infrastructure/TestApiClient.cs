using System.Text.Json;
using System.Text.Json.Serialization;
using Singulink.Net.Http.Api.Client.InternalServices;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// An API client that sends its requests to a <see cref="FakeHttpMessageHandler"/>, or through the shared handler if it is created without one, and exposes
/// the protected members of <see cref="ApiClientBase"/>.
/// </summary>
public sealed class TestApiClient : ApiClientBase
{
    public static Uri DefaultBaseAddress { get; } = new("https://api.example.com/v1/");

    public TestApiClient(HttpMessageHandler? handler, string? sessionToken = null, Action<string?>? sessionTokenChanged = null)
        : base(TestJsonContext.Default, sessionToken, sessionTokenChanged)
    {
        HttpHandler = handler;
        BaseAddress = DefaultBaseAddress;
    }

    public TestApiClient(
        TestApiClient parent, InterpolatedApiPathHandler basePath = default, object? userIdPrecondition = null, HttpMessageHandler? handler = null)
        : base(parent, basePath, userIdPrecondition)
    {
        if (handler is not null)
            HttpHandler = handler;
    }

    /// <summary>
    /// Gets or sets the base address. Uses the base class's base address when <see langword="null"/>, which is the default for child clients.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Gets or sets the session cookie name. Uses the base class's name when <see langword="null"/>.
    /// </summary>
    public string? CookieName { get; set; }

    /// <summary>
    /// Gets or sets the user ID precondition query parameter name. Uses the base class's name when <see langword="null"/>.
    /// </summary>
    public string? PreconditionName { get; set; }

    /// <summary>
    /// Gets or sets the request timeout. Uses the base class's timeout when <see langword="null"/>.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets the number of times <see cref="OnSessionEnded"/> was called on this client.
    /// </summary>
    public int SessionEndedCount { get; private set; }

    /// <summary>
    /// Gets or sets an action that <see cref="OnSessionEnded"/> runs.
    /// </summary>
    public Action? SessionEnded { get; set; }

    public TimeSpan CurrentRequestTimeout => RequestTimeout;

    public string CurrentSessionCookieName => SessionCookieName;

    public string CurrentUserAgent => UserAgent;

    public string CurrentUserIdPreconditionQueryName => UserIdPreconditionQueryName;

    public new string? SessionToken => base.SessionToken;

    public new bool IsPersistentSession => base.IsPersistentSession;

    public void ClearToken() => ClearSessionToken();

    public static void SetTimeout(HttpRequestMessage request, TimeSpan timeout) => SetRequestTimeout(request, timeout);

    public static string CreateAppUserAgent(string productName, string productVersion) => CreateUserAgent(productName, productVersion);

    public void NotifyStarted() => NotifySessionStarted();

    public Task SessionStartTask => NextSessionStart;

    public void EndSessionOnPlatform(bool isBrowser, string? sessionToken, Task sessionStart) => EndSession(isBrowser, sessionToken, sessionStart);

    public HttpRequestMessage CreateGetRequest(InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default) =>
        CreateRequest(HttpMethod.Get, path, query);

    public HttpRequestMessage CreatePostRequest(InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default) =>
        CreateRequest(HttpMethod.Post, path, query);

    public Uri GetUrl(InterpolatedApiPathHandler path, ReadOnlySpan<(string Name, object? Value)> query = default) => GetApiUrl(path, query);

    public Task SendRequestAsync(HttpRequestMessage request, object? content = null) => SendAsync(request, content);

    public Task SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken) => SendAsync(request, cancellationToken);

    public Task<TResponse> SendRequestAsync<TResponse>(HttpRequestMessage request, object? content = null)
        where TResponse : notnull
    {
        return SendAsync<TResponse>(request, content);
    }

    public IAsyncEnumerable<TItem> SendStreamingRequestAsync<TItem>(HttpRequestMessage request) => SendStreamingAsync<TItem>(request);

    public Task<TResult> UploadFileAsync<TResult>(
        InterpolatedApiPathHandler path,
        UploadSource source,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        object? command = null,
        UploadOptions? options = null,
        CancellationToken cancellationToken = default)
        where TResult : notnull
    {
        return UploadAsync<TResult>(path, source, query, command, options, cancellationToken);
    }

    public IAsyncEnumerable<TItem> UploadFileStreamingAsync<TItem>(
        InterpolatedApiPathHandler path,
        UploadSource source,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        object? command = null,
        UploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return UploadStreamingAsync<TItem>(path, source, query, command, options, cancellationToken);
    }

    protected override TimeSpan RequestTimeout => Timeout ?? base.RequestTimeout;

    protected override string SessionCookieName => CookieName ?? base.SessionCookieName;

    protected override string UserIdPreconditionQueryName => PreconditionName ?? base.UserIdPreconditionQueryName;

    protected override Uri GetBaseAddress() => BaseAddress ?? base.GetBaseAddress();

    protected override void OnSessionEnded()
    {
        SessionEndedCount++;
        SessionEnded?.Invoke();
    }
}

public sealed record TestItem(int Id, string Name);

/// <summary>
/// An item whose reading calls the action of <see cref="LateItemConverter.Reading"/>, such as one that advances the time.
/// </summary>
[JsonConverter(typeof(LateItemConverter))]
public sealed record LateItem(int Id);

public sealed class LateItemConverter : JsonConverter<LateItem>
{
    public static AsyncLocal<Action?> Reading { get; } = new();

    public override LateItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var item = JsonDocument.ParseValue(ref reader);
        Reading.Value?.Invoke();

        return new LateItem(item.RootElement.GetProperty("id").GetInt32());
    }

    public override void Write(Utf8JsonWriter writer, LateItem value, JsonSerializerOptions options) => throw new NotSupportedException();
}

[JsonSerializable(typeof(TestItem))]
[JsonSerializable(typeof(LateItem))]
[JsonSerializable(typeof(List<TestItem>))]
public sealed partial class TestJsonContext : JsonSerializerContext;
