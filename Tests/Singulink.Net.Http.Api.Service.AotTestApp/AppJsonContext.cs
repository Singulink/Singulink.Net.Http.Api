using System.Text.Json;
using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Service.AotTestApp;

/// <summary>
/// Serializes request and response bodies, including streamed items, and hub payloads. Minimal APIs look up the type info of each endpoint's declared
/// result type when the endpoints are built, so the context also has <see cref="IAsyncEnumerable{T}"/> of the streamed item type.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(CreateCodeRequest))]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(CodeResponse))]
[JsonSerializable(typeof(UserResponse))]
[JsonSerializable(typeof(ClientResponse))]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(IAsyncEnumerable<Item>))]
[JsonSerializable(typeof(PasskeySignInRequest))]
[JsonSerializable(typeof(PasskeyFinishRequest))]
[JsonSerializable(typeof(CeremonyResponse))]
[JsonSerializable(typeof(RegisteredResponse))]
[JsonSerializable(typeof(NoteRequest))]
[JsonSerializable(typeof(NoteResponse))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

internal sealed record CreateCodeRequest(string UserId, string CodeChallenge);

internal sealed record SignInRequest(string Code, string CodeVerifier);

internal sealed record CodeResponse(string Code);

internal sealed record UserResponse(string? UserId);

internal sealed record ClientResponse(string? Ip, string Scheme, string? Country);

internal sealed record Item(int Number, string UserId);

internal sealed record PasskeySignInRequest(string CodeChallenge);

internal sealed record PasskeyFinishRequest(string CeremonyId, string CredentialJson, string? CodeVerifier = null);

internal sealed record CeremonyResponse(string CeremonyId, string OptionsJson);

internal sealed record RegisteredResponse(string SuggestedName);

internal sealed record NoteRequest(string Title, string? Note);

internal sealed record NoteResponse(string Title, string? Note);
