using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Singulink.Net.Http.Api.Client;
using Singulink.Net.Http.Api.Service.Testing;

namespace Singulink.Net.Http.Api.Service.AotTestApp;

/// <summary>
/// Starts the app on a free local port and makes requests to it over HTTP: Cloudflare forwarding, the API null handling of request bodies, sessions
/// (binding, sign-in with a code, an unreadable cookie), a streaming response, a hub connection with the session (a method call, a message from the server
/// and errors), the toolkit's hub connection (the service's confirmation of a connection, and a refused one), a passkey registration and a passkey
/// sign-in, and the passkey page. Returns a non-zero exit code if a check fails.
/// </summary>
internal static class SelfCheck
{
    public const string Argument = "--self-check";
    public const string PasskeyOrigin = "https://localhost";
    public const int ItemCount = 3;

    private const string UserId = "alice";
    private const string SessionCookieName = "__Host-session-token";
    private const string WithUser = "?if-userId=" + UserId;

    public static async Task<int> RunAsync(WebApplication app, string keysPath)
    {
        try
        {
            await app.StartAsync();

            using var client = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AotSelfCheck/1.0");

            await new Checks(client).RunAsync();

            Console.WriteLine("Self-check passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Self-check failed: {ex}");
            return 1;
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();

            if (Directory.Exists(keysPath))
                Directory.Delete(keysPath, recursive: true);
        }
    }

    private sealed class Checks(HttpClient client)
    {
        private string? _sessionCookie;

        public async Task RunAsync()
        {
            await Step("Cloudflare forwarding applies the forwarded address and scheme and reads the location", async () => {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/client");
                request.Headers.Add("CF-Connecting-IP", "203.0.113.7");
                request.Headers.Add("X-Forwarded-Proto", "https");
                request.Headers.Add("CF-IPCountry", "CA");

                var result = await ReadAsync(await client.SendAsync(request), AppJsonContext.Default.ClientResponse);
                Ensure(result is { Ip: "203.0.113.7", Scheme: "https", Country: "CA" }, $"Unexpected client: {result}");
            });

            await Step("A request body without a nullable constructor parameter is accepted, and nulls are written in the response", async () => {
                using var response = await PostJsonAsync("/notes", """{"title":"t"}""");
                Ensure(response.StatusCode is HttpStatusCode.OK, $"Unexpected status: {response.StatusCode}");

                string body = await response.Content.ReadAsStringAsync();
                Ensure(body is """{"title":"t","note":null}""", $"Unexpected body: {body}");
            });

            await Step("A request body without a non-nullable constructor parameter is rejected", async () => {
                using var response = await PostJsonAsync("/notes", """{"note":"n"}""");
                Ensure(response.StatusCode is HttpStatusCode.BadRequest, $"Unexpected status: {response.StatusCode}");

                string error = await response.Content.ReadAsStringAsync();
                Ensure(error is "The request is not valid. Path: $ | LineNumber: 0 | BytePositionInLine: 12.", $"Unexpected error: {error}");
            });

            await Step("A required session token is rejected without a session", async () => {
                using var response = await SendAsync(HttpMethod.Get, "/me");
                Ensure(response.StatusCode is HttpStatusCode.Unauthorized, $"Unexpected status: {response.StatusCode}");

                string error = await response.Content.ReadAsStringAsync();
                Ensure(error.StartsWith($"[{ApiErrorCodes.SignInRequired}] ", StringComparison.Ordinal), $"Unexpected error: {error}");
            });

            await Step("An optional session token is null without a session", async () => {
                var user = await ReadAsync(await SendAsync(HttpMethod.Get, "/me/optional"), AppJsonContext.Default.UserResponse);
                Ensure(user.UserId is null, $"Unexpected user: {user}");
            });

            await Step("An unreadable session cookie is no session and is cleared", async () => {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/me/optional");
                request.Headers.Add("Cookie", $"{SessionCookieName}=not-a-session-token");

                using var response = await client.SendAsync(request);
                var user = await ReadAsync(response, AppJsonContext.Default.UserResponse, dispose: false);

                Ensure(user.UserId is null, $"Unexpected user: {user}");
                Ensure(GetSessionCookie(response) is "", "The session cookie was not cleared.");
            });

            await Step("Signing in with a sign-in code sets the session cookie", () => SignInWithCodeAsync(UserId));

            await Step("The session cookie authenticates requests", async () => {
                var user = await ReadAsync(await SendAsync(HttpMethod.Get, "/me" + WithUser), AppJsonContext.Default.UserResponse);
                Ensure(user.UserId is UserId, $"Unexpected user: {user}");

                user = await ReadAsync(await SendAsync(HttpMethod.Get, "/me/optional" + WithUser), AppJsonContext.Default.UserResponse);
                Ensure(user.UserId is UserId, $"Unexpected optional user: {user}");
            });

            await Step("A streaming response streams its items", async () => {
                using var response = await SendAsync(HttpMethod.Get, "/items" + WithUser);
                Ensure(response.IsSuccessStatusCode, $"Unexpected status: {response.StatusCode}");
                Ensure(response.Content.Headers.ContentType?.MediaType is StreamingResponse.MediaType, "Unexpected content type.");

                await using var stream = await response.Content.ReadAsStreamAsync();
                List<Item> items = [];

                await foreach (var item in StreamingResponse.ReadItemsAsync(stream, AppJsonContext.Default.Item))
                    items.Add(item);

                Ensure(items.Count is ItemCount && items.Select((item, i) => item == new Item(i + 1, UserId)).All(match => match), "Unexpected items.");
            });

            await Step("A hub connection with the session calls a hub method and receives a message from the server", async () => {
                await using var connection = CreateHubConnection();
                var echoed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.On<string>(AppHubContract.Echoed.Name, text => echoed.TrySetResult(text));

                await connection.StartAsync();

                string result = await connection.InvokeAsync<string>(AppHubContract.Echo.Name, "hello");
                Ensure(result is UserId + ": hello", $"Unexpected result: {result}");

                string message = await echoed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Ensure(message is "hello", $"Unexpected message: {message}");
            });

            await Step("Hub exceptions are reported as API errors", async () => {
                await using var connection = CreateHubConnection();
                await connection.StartAsync();

                try
                {
                    await connection.InvokeAsync<string>(AppHubContract.Fail.Name);
                    throw new InvalidOperationException("The hub method did not fail.");
                }
                catch (HubException ex)
                {
                    Ensure(ex.Message.Contains("409 [already-exists] Already exists.", StringComparison.Ordinal), $"Unexpected error: {ex.Message}");
                }

                List<string> items = [];

                try
                {
                    await foreach (string item in connection.StreamAsync<string>(AppHubContract.CountThenFail.Name, 2))
                        items.Add(item);

                    throw new InvalidOperationException("The stream did not fail.");
                }
                catch (HubException ex)
                {
                    Ensure(ex.Message.Contains("409 [stream-failed] Stream failed.", StringComparison.Ordinal), $"Unexpected stream error: {ex.Message}");
                }

                Ensure(items is ["1", "2"], $"Unexpected items: {string.Join(", ", items)}");
            });

            await Step("The toolkit's hub connection starts once the service confirms the connection, and a refused one fails its start", async () => {
                await using (var connection = new ApiHubConnection(CreateHubConnection()))
                {
                    await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Ensure(connection.State is HubConnectionState.Connected, $"Unexpected state: {connection.State}");

                    string result = await connection.InvokeAsync(AppHubContract.Echo, "confirmed");
                    Ensure(result is UserId + ": confirmed", $"Unexpected result: {result}");
                }

                await using var refused = new ApiHubConnection(CreateHubConnection(withSession: false));

                try
                {
                    await refused.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    throw new InvalidOperationException("The start did not fail.");
                }
                catch (UnauthorizedApiException ex)
                {
                    Ensure(ex.ErrorCode is ApiErrorCodes.SignInRequired, $"Unexpected error code: {ex.ErrorCode}");
                }

                Ensure(refused.State is HubConnectionState.Disconnected, $"Unexpected state of the refused connection: {refused.State}");
            });

            using var authenticator = new SoftwareAuthenticator(PasskeyOrigin);

            await Step("A passkey can be registered", async () => {
                var ceremony = await ReadAsync(
                    await SendAsync(HttpMethod.Post, "/passkeys/register/begin" + WithUser), AppJsonContext.Default.CeremonyResponse);

                string credential = authenticator.Create(ceremony.OptionsJson);

                var finish = new PasskeyFinishRequest(ceremony.CeremonyId, credential);
                var registered = await ReadAsync(
                    await SendAsync(HttpMethod.Post, "/passkeys/register/finish" + WithUser, finish, AppJsonContext.Default.PasskeyFinishRequest),
                    AppJsonContext.Default.RegisteredResponse);

                Ensure(registered.SuggestedName.Length > 0, "No suggested name.");
            });

            await Step("Signing in with the passkey continues with a sign-in code", async () => {
                var (verifier, challenge) = CreatePkce();
                _sessionCookie = null;

                var ceremony = await ReadAsync(
                    await SendAsync(
                        HttpMethod.Post, "/passkeys/sign-in/begin", new PasskeySignInRequest(challenge), AppJsonContext.Default.PasskeySignInRequest),
                    AppJsonContext.Default.CeremonyResponse);

                using (var options = JsonDocument.Parse(ceremony.OptionsJson))
                {
                    var root = options.RootElement;
                    Ensure(root.GetProperty("rpId").GetString() is "localhost", "Unexpected relying party ID.");
                    Ensure(root.GetProperty("userVerification").GetString() is "required", "Unexpected user verification.");
                    Ensure(root.GetProperty("challenge").GetString() is { Length: > 0 }, "No challenge.");
                }

                var finish = new PasskeyFinishRequest(ceremony.CeremonyId, authenticator.Get(ceremony.OptionsJson), verifier);
                var code = await ReadAsync(
                    await SendAsync(HttpMethod.Post, "/passkeys/sign-in/finish", finish, AppJsonContext.Default.PasskeyFinishRequest),
                    AppJsonContext.Default.CodeResponse);

                await RedeemAsync(code.Code, verifier, UserId);
            });

            await Step("The passkey page serves its files", async () => {
                using var response = await SendAsync(HttpMethod.Get, "/passkey/passkey-page.js");
                Ensure(response.IsSuccessStatusCode, $"Unexpected status: {response.StatusCode}");
                Ensure((await response.Content.ReadAsStringAsync()).Length > 0, "Empty script.");
            });
        }

        private static async Task Step(string name, Func<Task> check)
        {
            try
            {
                await check();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Check failed: {name}.", ex);
            }

            Console.WriteLine($"Passed: {name}.");
        }

        private static void Ensure(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private static (string Verifier, string Challenge) CreatePkce()
        {
            string verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            string challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            return (verifier, challenge);
        }

        private static string? GetSessionCookie(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values))
                return null;

            string? cookie = values.LastOrDefault(v => v.StartsWith(SessionCookieName + "=", StringComparison.Ordinal));
            return cookie?[(SessionCookieName.Length + 1)..].Split(';')[0];
        }

        private async Task SignInWithCodeAsync(string userId)
        {
            var (verifier, challenge) = CreatePkce();
            var request = new CreateCodeRequest(userId, challenge);
            var code = await ReadAsync(
                await SendAsync(HttpMethod.Post, "/sign-in/code", request, AppJsonContext.Default.CreateCodeRequest), AppJsonContext.Default.CodeResponse);

            await RedeemAsync(code.Code, verifier, userId);
        }

        private async Task RedeemAsync(string code, string verifier, string userId)
        {
            using var response = await SendAsync(HttpMethod.Post, "/sign-in", new SignInRequest(code, verifier), AppJsonContext.Default.SignInRequest);
            var user = await ReadAsync(response, AppJsonContext.Default.UserResponse, dispose: false);

            Ensure(user.UserId == userId, $"Unexpected user: {user}");
            _sessionCookie = GetSessionCookie(response);
            Ensure(_sessionCookie is { Length: > 0 }, "No session cookie was set.");
        }

        private HubConnection CreateHubConnection(bool withSession = true)
        {
            return new HubConnectionBuilder()
                .WithUrl(new Uri(client.BaseAddress!, "hub"), options => {
                    if (withSession)
                        options.Headers["Cookie"] = $"{SessionCookieName}={_sessionCookie}";

                    options.Headers["User-Agent"] = "AotSelfCheck/1.0";
                })
                .AddJsonProtocol(options => options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default))
                .Build();
        }

        private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path) => SendAsync<object>(method, path, null, null);

        private Task<HttpResponseMessage> PostJsonAsync(string path, string json)
        {
            return client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        }

        private async Task<HttpResponseMessage> SendAsync<T>(HttpMethod method, string path, T? body, JsonTypeInfo<T>? typeInfo)
        {
            using var request = new HttpRequestMessage(method, path);

            if (typeInfo is not null)
                request.Content = JsonContent.Create(body, typeInfo);

            if (_sessionCookie is not null)
                request.Headers.Add("Cookie", $"{SessionCookieName}={_sessionCookie}");

            return await client.SendAsync(request);
        }

        private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, bool dispose = true)
        {
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync();
                    throw new InvalidOperationException($"{(int)response.StatusCode} response from {response.RequestMessage?.RequestUri}: {error}");
                }

                return await response.Content.ReadFromJsonAsync(typeInfo) ?? throw new InvalidOperationException("Empty response.");
            }
            finally
            {
                if (dispose)
                    response.Dispose();
            }
        }
    }
}
