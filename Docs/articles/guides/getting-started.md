<div class="article">

# Getting Started

This guide walks through a minimal service and a minimal client built with the toolkit, and points to the in-depth guides for each area.

### Packages

A service references `Singulink.Net.Http.Api.Service`. A client references `Singulink.Net.Http.Api.Client` (and `Singulink.Net.Http.Api.Client.SignalR` if it uses hubs). Both pull in the shared `Singulink.Net.Http.Api` package, which a data contracts assembly that both sides share also references when it declares hub contracts or uses the toolkit's data contracts, such as those of the sign-in steps.

### How the pieces fit

A request from a client built on <xref:Singulink.Net.Http.Api.Client.ApiClientBase> carries the session cookie, and once the client knows who is signed in, a user ID precondition. On the service, <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*> installs the exception middleware and streaming conversion, and endpoints receive the session token as a parameter. Errors thrown as <xref:Singulink.Net.Http.Api.ApiException> types become error responses, and the client rethrows them as the same types. See [Architecture and Conventions](../concepts/architecture.md) for the full picture.

## Setting Up a Service

The service side needs six registrations and two pipeline calls. Session handling is optional but is shown here since most services need it; see [Session Handling](session-handling.md) for the types it requires.

```csharp
var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// Hosts allowed to make credentialed cross-origin requests over https (also used to block CSRF). Loopback hosts also match http, so localhost is
// only allowed in development.
string[] allowedOrigins = builder.Environment.IsDevelopment() ? ["example.com", "*.example.com", "localhost"] : ["example.com", "*.example.com"];
services.AddAllowedOrigins(allowedOrigins);

// Session cookies are encrypted with data protection. This keeps its keys in a folder that survives restarts and redeployments.
services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo("/srv/example-api/keys"));

// Sessions: your token type and session store.
services.AddHttpSessionHandling<SessionToken, SessionStore>();

// Maps exceptions to error responses and logs unexpected ones with a reference ID.
services.AddApiExceptionHandler();

// The services of UseApiResponseHandling, which converts the streaming endpoints at startup.
services.AddApiResponseHandling();

// The JSON of requests, responses and hub payloads: your serializer context, with the toolkit's null handling.
services.AddApiJson(AppSerializerContext.Default);

var app = builder.Build();

app.UseApiResponseHandling();
app.UseAllowedOrigins();

app.MapGet("/documents/{id}", GetDocumentAsync);
app.MapPost("/sessions", SignInAsync);

await app.RunAsync();
```

The registrations are:

- <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddAllowedOrigins*> registers the origins that may make requests, as host patterns: a host matches its `https` origins on any port, and loopback hosts such as `localhost` also match `http`, which is why the sample only allows `localhost` in development. Outside the Development environment, <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*> logs a warning for allowed origins that match a loopback host. A pattern with a scheme or port, such as `http://intranet.example.com`, matches only that scheme or port (see <xref:Singulink.Net.Http.Api.Service.OriginValidator>).
- <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddHttpSessionHandling*> registers the session services, which check requests against the allowed origins. Session cookies are encrypted with ASP.NET Core data protection, which session handling registers if the app has not, and the sample's `PersistKeysToFileSystem` chooses where its keys are kept (see the note in [Session Handling](session-handling.md#the-session-store)).
- <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiExceptionHandler*> registers the default <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler>, which also reports requests that endpoints cannot read, such as a malformed body, as API errors.
- <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiResponseHandling*> registers the services that <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*> requires, and the app fails to start if it registers them without calling it.
- <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiJson*> adds the app's serializer context to the JSON options of requests, responses and hub payloads, with null handling that rejects a missing or `null` value where the contract does not allow one (see [JSON Serialization](json-serialization.md)).

In the pipeline, <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*> installs <xref:Singulink.Net.Http.Api.Service.ApiExceptionMiddleware>, which handles the exceptions of the endpoints and of the middleware after it, and converts the streaming endpoints, whether they are mapped before or after it. <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*> configures CORS from the registered origins.

#### Pipeline order

Middleware runs in the order it is added. With the toolkit's middleware and the ASP.NET Core middleware that services usually add, the order is:

1. <xref:Singulink.Net.Http.Api.Service.CloudflareWebApplicationExtensions.UseCloudflareForwarding*> for a service behind Cloudflare (see [Cloudflare](cloudflare.md)), or ASP.NET Core's `UseForwardedHeaders` for one behind another proxy. The middleware after it sees the client's address and scheme rather than the proxy's.
2. <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*>, which reports the exceptions of the middleware after it and of the endpoints as API errors.
3. `UseHttpsRedirection`, if the service redirects requests to HTTPS.
4. <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*>, which adds the CORS headers that let browser pages on other origins read the responses of the middleware after it, such as a rate limit rejection.
5. `UseRateLimiter`, for rate limits (see [Rate Limiting](rate-limiting.md)).

Endpoints are not middleware, so they are mapped before or after any of these calls.

#### Writing an endpoint

Endpoints are ordinary minimal API handlers. The session token is bound as a parameter (a non-nullable parameter requires a signed-in user), and failures are thrown as exceptions:

```csharp
static async Task<DocumentInfo> GetDocumentAsync(long id, SessionToken sessionToken, AppDbContext db)
{
    var document = await db.Documents.FindAsync(id)
        ?? throw new NotFoundApiException("The document does not exist.");

    if (document.OwnerId != sessionToken.UserId)
        throw new ForbiddenApiException("You do not have access to this document.");

    return new DocumentInfo(document.Id, document.Name);
}
```

Throwing <xref:Singulink.Net.Http.Api.NotFoundApiException> produces a 404 response whose body is the message, and a client built with the toolkit rethrows it as the same exception type. See [Exception Handling](exception-handling.md) for the full hierarchy, error codes and logging.

## Building a Client

A client derives from <xref:Singulink.Net.Http.Api.Client.ApiClientBase>, provides the base address, and exposes methods that send requests. Once the user is signed in, a child client created with the user's ID adds the user ID precondition to its requests:

```csharp
public sealed class MyApiClient : ApiClientBase
{
    public MyApiClient(string? sessionToken, Action<string?>? sessionTokenChanged)
        : base(AppSerializerContext.Default, sessionToken, sessionTokenChanged) { }

    protected override Uri GetBaseAddress() => new("https://api.example.com/");

    public async Task<UserApiClient> SignInAsync(SignInCommand command)
    {
        var session = await PostAsync<CurrentSessionInfo>("sessions", command);
        return new UserApiClient(this, session.UserId);
    }
}

public sealed class UserApiClient : ApiClientBase
{
    internal UserApiClient(MyApiClient parent, int userId) : base(parent, userIdPrecondition: userId) { }

    public Task<DocumentInfo> GetDocumentAsync(long id) => GetAsync<DocumentInfo>($"documents/{id}");
}
```

The request helpers, such as <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetAsync*?displayProperty=nameWithType> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.PostAsync*?displayProperty=nameWithType>, send a request to a path relative to the base address with the session cookie, capture a refreshed cookie from the response, and throw the appropriate <xref:Singulink.Net.Http.Api.ApiException> for error responses:

```csharp
try
{
    var document = await client.GetDocumentAsync(42);
}
catch (NotFoundApiException ex)
{
    ShowMessage(ex.Message);
}
catch (UnauthorizedApiException ex) when (ex.ErrorCode == ApiErrorCodes.SignInRequired)
{
    NavigateToSignIn();
}
```

A `401` with the `sign-in-required` error code (<xref:Singulink.Net.Http.Api.ApiErrorCodes.SignInRequired>) means that the request has no session. Other `401` errors, such as a rejected sign-in credential, don't. The session token and change callback passed to the constructor let the app keep a persistent session across restarts. A client constructed with a serializer context has the same null handling as the service. See [Building API Clients](api-clients.md) for serializer options, request paths, session persistence, child clients, user ID preconditions, signing in and restoring a session at startup, and platform behavior.

## Next Steps

Continue with these related articles:

- [Session Handling](session-handling.md) - Implementing the token and the session store, and retrieving the session in endpoints.
- [Exception Handling](exception-handling.md) - The exception hierarchy, error codes, and mapping unexpected exceptions.
- [JSON Serialization](json-serialization.md) - The app's serializer context and the null handling on the service and in clients.
- [Streaming Responses](streaming-responses.md) - Returning `IAsyncEnumerable<T>` from endpoints and consuming it on the client.
- [SignalR Hubs](signalr-hubs.md) - Strongly typed hub contracts and connections.
- [Sign-In Codes and Device Linking](sign-in-codes.md) - One-time sign-in codes, device links and short codes.
- [Passkeys](passkeys.md) - Passkey sign-in, verification and registration.
- [Chunked Uploads](uploads.md) - Receiving files in chunks, with a handler that runs once the file is complete.
- [Rate Limiting](rate-limiting.md) - Per-client limits with internal clients exempt.
- [Cloudflare](cloudflare.md) - Restoring the client's address and scheme for services behind Cloudflare.
- [Running Multiple Servers](multiple-servers.md) - What servers share, and the short-lived store.
- [Trimming and Native AOT](trimming-and-aot.md) - What the packages need from trimmed and Native AOT apps.
- [Architecture and Conventions](../concepts/architecture.md) - What each package contains and why the conventions look the way they do.
- [Upgrading from 3.x](upgrading.md) - What changes for an app written for 3.x.

</div>
