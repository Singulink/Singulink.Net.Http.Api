using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.Service.Testing;

namespace Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;

/// <summary>
/// Passkey services for a relying party at <c>example.com</c>, backed by an in-memory passkey store and a manual clock. Each call to
/// <see cref="CreateService"/> stands in for a separate request. With a <see cref="StoreKind.Shared"/> short-lived store, ceremonies are kept in a store
/// that stands in for shared storage, so every request could run on a different server.
/// </summary>
public sealed class PasskeyFixture : IDisposable
{
    public const string RpId = "example.com";
    public const string Origin = "https://app.example.com";

    private readonly ServiceProvider _provider;
    private readonly List<IServiceScope> _scopes = [];

    public PasskeyFixture(Action<PasskeyOptions>? configure = null, StoreKind storeKind = StoreKind.Memory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddLogging(builder => builder.AddProvider(Logs));

        if (storeKind is StoreKind.Shared)
            services.AddSingleton<IShortLivedStore>(new SharedShortLivedStore(Time));

        services.AddPasskeys<InMemoryPasskeyStore>(o => {
            o.RelyingPartyId = RpId;
            o.RelyingPartyName = "Example";
            o.AllowedOrigins.Add(Origin);
            configure?.Invoke(o);
        });

        // Every request shares the one store, as they would share a database.
        services.AddScoped<IPasskeyStore>(_ => Store);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        ShortLivedStore = _provider.GetRequiredService<IShortLivedStore>();
    }

    public InMemoryPasskeyStore Store { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public IShortLivedStore ShortLivedStore { get; }

    public ManualTimeProvider Time { get; } = new();

    /// <summary>
    /// Gets the PKCE pair that the fixture's sign-ins are bound to.
    /// </summary>
    public (string Verifier, string Challenge) Pkce { get; } = CreatePkce();

    public static PasskeyUser User(string id = "user-1") => new(id, $"{id}@example.com", $"User {id}");

    public static (string Verifier, string Challenge) CreatePkce()
    {
        string verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return (verifier, challenge);
    }

    public PasskeyService CreateService()
    {
        var scope = _provider.CreateScope();

        lock (_scopes)
            _scopes.Add(scope);

        return scope.ServiceProvider.GetRequiredService<PasskeyService>();
    }

    /// <summary>
    /// Creates the sign-in codes service, as a request on any server would get it.
    /// </summary>
    public SignInCodes CreateSignInCodes() => _provider.GetRequiredService<SignInCodes>();

    /// <summary>
    /// Registers a passkey on the authenticator for the user and adds it to the store.
    /// </summary>
    public async Task<RegisteredPasskey> RegisterAsync(SoftwareAuthenticator authenticator, string userId = "user-1", string? device = null)
    {
        var ceremony = await CreateService().BeginRegistrationAsync(User(userId));
        var result = await CreateService().FinishRegistrationAsync(userId, ceremony.Id, authenticator.Create(ceremony.OptionsJson), device);
        Store.Add(result.Passkey, result.SuggestedName);

        return result;
    }

    /// <summary>
    /// Signs in with the authenticator using a discoverable credential ceremony bound to the fixture's PKCE pair.
    /// </summary>
    public async Task<VerifiedPasskey> SignInAsync(SoftwareAuthenticator authenticator)
    {
        var ceremony = await CreateService().BeginSignInAsync(codeChallenge: Pkce.Challenge);
        return await CreateService().FinishSignInAsync(ceremony.Id, authenticator.Get(ceremony.OptionsJson), Pkce.Verifier);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
            scope.Dispose();

        _provider.Dispose();
    }
}
