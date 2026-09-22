<div class="article">

# Getting Started

This guide walks through a minimal service and a minimal client built with the toolkit, and points to the in-depth guides for each area.

### Packages

A service references `Singulink.Net.Http.Api.Service`. A client references `Singulink.Net.Http.Api.Client` (and `Singulink.Net.Http.Api.Client.SignalR` if it uses hubs). Both pull in the shared `Singulink.Net.Http.Api` package, which is also referenced directly by any data contracts assembly that declares hub contracts, since those are shared between the two sides.

### How the pieces fit

A request from a client built on <xref:Singulink.Net.Http.Api.Client.ApiClientBase> carries the session cookie and a user ID precondition. On the service, <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*> installs the exception middleware and streaming conversion, and endpoints receive the session token as a parameter. Errors thrown as <xref:Singulink.Net.Http.Api.ApiException> types become error responses, and the client rethrows them as the same types. See [Architecture and Conventions](../concepts/architecture.md) for the full picture.

## Setting Up a Service

The service side needs three registrations and two pipeline calls. Session handling is optional but is shown here since most services need it; see [Session Handling](session-handling.md) for the types it requires.

```csharp
var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// Origins allowed to make credentialed cross-origin requests (also used to block CSRF).
services.AddAllowedOrigins("example.com", "*.example.com", "localhost");

// Sessions: your token type, session data type and store context factory.
services.AddHttpSessionHandling<SessionToken, Session, SessionStoreContextFactory>();

// Maps exceptions to error responses and logs unexpected ones with a reference ID.
services.AddApiExceptionHandler();

var app = builder.Build();

app.UseAllowedOrigins();

app.MapGet("/documents/{id}", GetDocumentAsync);
app.MapPost("/sessions", SignInAsync);

// Must be called after all endpoints are mapped.
app.UseApiResponseHandling();

await app.RunAsync();
```

<xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddAllowedOrigins*> registers the origins that may make requests. <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddHttpSessionHandling*> registers the session services, and <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiExceptionHandler*> registers the default <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler>. In the pipeline, <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*> configures CORS from the registered origins and <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*> installs <xref:Singulink.Net.Http.Api.Service.ApiExceptionMiddleware> and converts streaming endpoints.

> [!IMPORTANT]
> <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*> wraps the endpoints that have already been mapped, so it must be called after all `Map` calls.

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

A client derives from <xref:Singulink.Net.Http.Api.Client.ApiClientBase>, provides the base address, and exposes methods that create requests and send them:

```csharp
public sealed class MyApiClient : ApiClientBase
{
    public MyApiClient(string? sessionToken, Action<string?>? sessionTokenChanged)
        : base(AppSerializerContext.Default, sessionToken, sessionTokenChanged) { }

    protected override Uri GetBaseAddress() => new("https://api.example.com/");

    public async Task SignInAsync(SignInCommand command)
    {
        IsPersistentSession = command.RememberMe;
        await SendAsync(CreateRequest(HttpMethod.Post, "sessions"), command);
    }

    public Task<DocumentInfo> GetDocumentAsync(long id)
    {
        return SendAsync<DocumentInfo>(CreateRequest(HttpMethod.Get, $"documents/{id}"));
    }
}
```

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateRequest*?displayProperty=nameWithType> builds a request against the base address, and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendAsync*?displayProperty=nameWithType> sends it, applies the session cookie, captures a refreshed cookie from the response, and throws the appropriate <xref:Singulink.Net.Http.Api.ApiException> for error responses:

```csharp
try
{
    var document = await client.GetDocumentAsync(42);
}
catch (NotFoundApiException ex)
{
    ShowMessage(ex.Message);
}
catch (UnauthorizedApiException)
{
    NavigateToSignIn();
}
```

The session token and change callback passed to the constructor let the app persist a signed-in session across restarts. See [Building API Clients](api-clients.md) for serializer options, user ID preconditions, child clients and platform behavior.

## Next Steps

Continue with these related articles:

- [Session Handling](session-handling.md) - Implementing the token, session data and store, and retrieving the session in endpoints.
- [Exception Handling](exception-handling.md) - The exception hierarchy, error codes, and mapping unexpected exceptions.
- [Streaming Responses](streaming-responses.md) - Returning `IAsyncEnumerable<T>` from endpoints and consuming it on the client.
- [SignalR Hubs](signalr-hubs.md) - Strongly typed hub contracts and connections.
- [Architecture and Conventions](../concepts/architecture.md) - What each package contains and why the conventions look the way they do.

</div>
