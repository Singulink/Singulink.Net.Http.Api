using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.DataProtection;
using Singulink.Net.Http.Api.Service;
using Singulink.Net.Http.Api.Service.AotTestApp;
using Singulink.Net.Http.Api.Service.Passkeys;
using Singulink.Net.Http.Api.Service.SignIn;

bool selfCheck = args.Contains(SelfCheck.Argument);
var builder = WebApplication.CreateSlimBuilder([.. args.Where(a => a != SelfCheck.Argument)]);
string? keysPath = null;

if (selfCheck)
{
    // A free local port, and data protection keys that are deleted afterwards.
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

    // SignalR logs the hub errors that the self-check causes on purpose, and the allowed origins include localhost, which the self-check runs on.
    builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", LogLevel.Critical);
    builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR.HubConnectionHandler", LogLevel.Critical);
    builder.Logging.AddFilter("Singulink.Net.Http.Api.Service.AllowedOrigins", LogLevel.Error);
    keysPath = Path.Combine(Path.GetTempPath(), $"httpapi-aot-keys-{Guid.NewGuid():N}");
}

var dataProtection = builder.Services.AddDataProtection();

if (keysPath is not null)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Request and response bodies, streamed items and hub payloads, with the API null handling.
builder.Services.AddApiJson(AppJsonContext.Default);

builder.Services.AddAllowedOrigins("localhost");
builder.Services.AddApiExceptionHandler();
builder.Services.AddApiResponseHandling();

builder.Services.AddSingleton<MemorySessions>();
builder.Services.AddHttpSessionHandling<AppSessionToken, MemorySessionStore>(SessionTokenJsonContext.Default.AppSessionToken);
builder.Services.AddSignInCodes();

builder.Services.AddSingleton<MemoryPasskeys>();
builder.Services.AddPasskeys<MemoryPasskeyStore>(options => {
    options.RelyingPartyId = "localhost";
    options.RelyingPartyName = "AOT Test";
    options.AllowedOrigins.Add(SelfCheck.PasskeyOrigin);
});

builder.Services.AddSignalR(options => options.AddApiExceptionFilter());

builder.Services.AddCloudflareForwarding(options => {
    options.DownloadRanges = false;
    options.SavedRangesPath = Path.Combine(AppContext.BaseDirectory, "cloudflare-ranges.txt");
});

var app = builder.Build();

app.UseCloudflareForwarding();
app.UseAllowedOrigins();

app.MapGet("/client", (HttpContext context) =>
    new ClientResponse(context.Connection.RemoteIpAddress?.ToString(), context.Request.Scheme, context.GetClientLocation()?.CountryCode));

app.MapPost("/sign-in/code", async (CreateCodeRequest request, SignInCodes codes) =>
    new CodeResponse(await codes.CreateAsync(request.UserId, request.CodeChallenge)));

app.MapPost("/sign-in", async (SignInRequest request, SessionContext<AppSessionToken> session, SignInCodes codes, MemorySessions sessions) => {
    var token = await codes.SignInAsync(session, request.Code, request.CodeVerifier, persistent: true,
        (code, info) => Task.FromResult(sessions.Create(code.UserId, info)));

    return new UserResponse(token.UserId);
});

app.MapGet("/me", (AppSessionToken token) => new UserResponse(token.UserId));
app.MapGet("/me/optional", (AppSessionToken? token) => new UserResponse(token?.UserId));
app.MapGet("/items", (AppSessionToken token) => GetItemsAsync(token.UserId));
app.MapPost("/notes", (NoteRequest request) => new NoteResponse(request.Title, request.Note));

app.MapPost("/passkeys/register/begin", async (AppSessionToken token, PasskeyService passkeys) => {
    var options = await passkeys.BeginRegistrationAsync(new PasskeyUser(token.UserId, token.UserId, token.UserId));
    return new CeremonyResponse(options.Id, options.OptionsJson);
});

app.MapPost("/passkeys/register/finish", async (PasskeyFinishRequest request, SessionContext<AppSessionToken> session, PasskeyService passkeys,
    MemoryPasskeys store) => {
    var token = await session.GetRequiredTokenAsync();
    var registered = await passkeys.FinishRegistrationAsync(token.UserId, request.CeremonyId, request.CredentialJson, session.Device);
    store.Add(registered.Passkey);

    return new RegisteredResponse(registered.SuggestedName);
});

app.MapPost("/passkeys/sign-in/begin", async (PasskeySignInRequest request, PasskeyService passkeys) => {
    var options = await passkeys.BeginSignInAsync(codeChallenge: request.CodeChallenge);
    return new CeremonyResponse(options.Id, options.OptionsJson);
});

app.MapPost("/passkeys/sign-in/finish", async (PasskeyFinishRequest request, PasskeyService passkeys, SignInCodes codes) => {
    var verified = await passkeys.FinishSignInAsync(request.CeremonyId, request.CredentialJson, request.CodeVerifier);
    return new CodeResponse(await codes.CreateAsync(verified));
});

app.MapPasskeyPage();
app.MapHub<AppHub>("/hub");

app.UseApiResponseHandling();

if (!selfCheck)
{
    await app.RunAsync();
    return 0;
}

return await SelfCheck.RunAsync(app, keysPath!);

static async IAsyncEnumerable<Item> GetItemsAsync(string userId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    for (int i = 1; i <= SelfCheck.ItemCount; i++)
    {
        await Task.Delay(10, cancellationToken);
        yield return new Item(i, userId);
    }
}
