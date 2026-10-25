namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a started passkey ceremony: the ID to finish it with, the WebAuthn options for the browser and how long it can be finished.
/// </summary>
public sealed class PasskeyCeremony
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyCeremony"/> class.
    /// </summary>
    /// <param name="id">The ceremony ID.</param>
    /// <param name="optionsJson">The WebAuthn options for the browser as JSON.</param>
    /// <param name="lifetime">How long the ceremony can be finished after it started.</param>
    public PasskeyCeremony(string id, string optionsJson, TimeSpan lifetime)
    {
        Id = id;
        OptionsJson = optionsJson;
        Lifetime = lifetime;
    }

    /// <summary>
    /// Gets the ceremony ID, which the client sends back with the credential to finish the ceremony. It is a random secret that can be used once.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the WebAuthn options for the browser as JSON: a <c>PublicKeyCredentialRequestOptionsJSON</c> object for sign-ins and verifications, or a
    /// <c>PublicKeyCredentialCreationOptionsJSON</c> object for registrations. Browsers read it with
    /// <c>PublicKeyCredential.parseRequestOptionsFromJSON</c> or <c>PublicKeyCredential.parseCreationOptionsFromJSON</c>.
    /// </summary>
    public string OptionsJson { get; }

    /// <summary>
    /// Gets how long the ceremony can be finished after the service started it. Counted from when the client receives the ceremony, it ends later than on
    /// the service by the time the response took to arrive. The browser's prompt can stay open for up to the <c>timeout</c> in <see cref="OptionsJson"/>,
    /// so the ceremony can expire while a prompt that started less than that before its end is still open.
    /// </summary>
    public TimeSpan Lifetime { get; }
}
