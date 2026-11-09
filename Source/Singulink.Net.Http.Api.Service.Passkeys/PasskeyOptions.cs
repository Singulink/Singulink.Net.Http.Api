using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Singulink.Net.Http.Api.Service.Passkeys;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides the relying party settings and ceremony behavior for <see cref="PasskeyService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The passkey service checks the options when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>. <see cref="AllowedOrigins"/> is then read-only, and changing it throws a <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// The options can be bound from configuration.
/// </para>
/// </remarks>
public sealed class PasskeyOptions
{
    // WebAuthn options carry the timeout in milliseconds as an unsigned 32-bit number.
    private static readonly TimeSpan MaxCeremonyTimeout = TimeSpan.FromMilliseconds(uint.MaxValue);

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyOptions"/> class.
    /// </summary>
    public PasskeyOptions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyOptions"/> class with the relying party ID and name.
    /// </summary>
    /// <param name="relyingPartyId">The relying party ID (see <see cref="RelyingPartyId"/>).</param>
    /// <param name="relyingPartyName">The relying party name (see <see cref="RelyingPartyName"/>).</param>
    /// <exception cref="ArgumentException"><paramref name="relyingPartyId"/> or <paramref name="relyingPartyName"/> is empty or consists only of white-space
    /// characters.</exception>
    [SetsRequiredMembers]
    public PasskeyOptions(string relyingPartyId, string relyingPartyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relyingPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relyingPartyName);

        RelyingPartyId = relyingPartyId;
        RelyingPartyName = relyingPartyName;
    }

    /// <summary>
    /// Gets or sets the relying party ID, a domain such as <c>example.com</c>, or <see langword="null"/> while it is not set. It is required, and has no
    /// default.
    /// </summary>
    /// <remarks>
    /// Passkeys are bound to it, so it must not change once passkeys exist. Ceremonies can only run on pages whose host is the relying party ID or a
    /// subdomain of it, so a relying party ID of <c>example.com</c> covers <c>example.com</c>, <c>app.example.com</c> and <c>api.example.com</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is empty or consists only of white-space characters.</exception>
    [MaybeNull]
    public required string RelyingPartyId {
        get => field ?? (IsFrozen ? throw new InvalidOperationException(NotSetMessage(nameof(RelyingPartyId))) : null);
        set {
            EnsureNotFrozen();
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the relying party name that browsers and authenticators may show when a passkey is created, or <see langword="null"/> while it is not
    /// set. It is required, and has no default.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty or consists only of white-space characters.</exception>
    [MaybeNull]
    public required string RelyingPartyName {
        get => field ?? (IsFrozen ? throw new InvalidOperationException(NotSetMessage(nameof(RelyingPartyName))) : null);
        set {
            EnsureNotFrozen();
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets the exact origins of the pages that run ceremonies, such as <c>https://app.example.com</c> for a web app and <c>https://api.example.com</c> for
    /// a passkey page served by the API.
    /// </summary>
    /// <remarks>
    /// Native Android apps that use the platform passkey APIs report an <c>android:apk-key-hash:...</c> origin, which can be added as is. The host of each
    /// origin, other than an <c>android:apk-key-hash:</c> one, must be the relying party ID or a subdomain of it, which is checked when the application
    /// starts.
    /// </remarks>
    public IList<string> AllowedOrigins { get; private set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether pages served from <c>http://localhost</c> or <c>https://localhost</c> on any port can run ceremonies.
    /// Default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Browsers only run ceremonies on pages whose host is the relying party ID or a subdomain of it, so pages on <c>localhost</c> can only run them with
    /// the relying party ID <c>localhost</c>. A different relying party ID with this set to <see langword="true"/> is logged as a warning when the
    /// application starts.
    /// </remarks>
    public bool AllowLocalhostOrigins {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how long the browser waits for the user to complete a ceremony. Must be greater than zero and at most <see cref="uint.MaxValue"/>
    /// milliseconds (about 49.7 days). Default is 3 minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative, or longer than <see cref="uint.MaxValue"/> milliseconds.</exception>
    public TimeSpan CeremonyTimeout {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxCeremonyTimeout, nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Gets or sets how long a started ceremony can be finished, which the ceremony reports as
    /// <see cref="DataContracts.PasskeyCeremony.Lifetime"/>. Must be at least <see cref="CeremonyTimeout"/>. Default is 5 minutes.
    /// </summary>
    /// <remarks>
    /// That it is at least <see cref="CeremonyTimeout"/> is checked when the application starts.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan CeremonyLifetime {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets whether the authenticator verifies the user, with a biometric, PIN or screen lock, in a ceremony: it must
    /// (<see cref="PasskeyRequirement.Required"/>), should (<see cref="PasskeyRequirement.Preferred"/>), or should only when it must
    /// (<see cref="PasskeyRequirement.Discouraged"/>). Default is <see cref="PasskeyRequirement.Required"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a value of <see cref="PasskeyRequirement"/>.</exception>
    public PasskeyRequirement UserVerification {
        get;
        set {
            EnsureNotFrozen();
            CheckRequirement(value);
            field = value;
        }
    } = PasskeyRequirement.Required;

    /// <summary>
    /// Gets or sets a value indicating whether every sign-in must be bound to a PKCE code challenge when it begins (see
    /// <see cref="PasskeyService.BeginSignInAsync"/>). Default is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A credential that passes through another app, such as the passkey hand-off page returning it to a native app's callback address, could be intercepted
    /// on its way. A sign-in bound to the code challenge of the app that began it cannot be finished with an intercepted credential (see
    /// <see cref="PasskeyPageOptions.RedirectSchemes"/> for what it does not prevent). When this is <see langword="false"/>, sign-ins can begin without a
    /// challenge.
    /// </remarks>
    public bool RequireCodeChallenge {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    } = true;

    /// <summary>
    /// Gets or sets whether new passkeys are discoverable credentials, which let users sign in without entering a user name first: they must be
    /// (<see cref="PasskeyRequirement.Required"/>), should be (<see cref="PasskeyRequirement.Preferred"/>), or should only be when the authenticator only
    /// makes such credentials (<see cref="PasskeyRequirement.Discouraged"/>). Default is <see cref="PasskeyRequirement.Required"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a value of <see cref="PasskeyRequirement"/>.</exception>
    public PasskeyRequirement DiscoverableCredentials {
        get;
        set {
            EnsureNotFrozen();
            CheckRequirement(value);
            field = value;
        }
    } = PasskeyRequirement.Required;

    /// <summary>
    /// Gets a value indicating whether the options are in use: the passkey service checked them (see <see cref="Validate"/>) when it took them, and they can no
    /// longer be changed.
    /// </summary>
    [MemberNotNullWhen(true, nameof(RelyingPartyId), nameof(RelyingPartyName))]
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as the passkey service does when it takes them. The required <see cref="RelyingPartyId"/> and <see cref="RelyingPartyName"/> are set,
    /// the relying party ID is a domain name, the <see cref="AllowedOrigins"/> are origins on it, of which there is one at least unless
    /// <see cref="AllowLocalhostOrigins"/> is set, and <see cref="CeremonyLifetime"/> is at least <see cref="CeremonyTimeout"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(RelyingPartyId), nameof(RelyingPartyName))]
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0 || RelyingPartyId is null || RelyingPartyName is null)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when the passkey service takes them. Options that are
    /// frozen already stay as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(RelyingPartyId), nameof(RelyingPartyName))]
    internal void Freeze()
    {
        if (IsFrozen)
            return;

        Validate();
        AllowedOrigins = new ReadOnlyCollection<string>([.. AllowedOrigins]);
        IsFrozen = true;
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        const string AndroidOriginPrefix = PasskeyOptionsValidator.AndroidOriginPrefix;
        List<string> problems = [];
        string? rpId = RelyingPartyId;
        bool isValidRpId = false;

        if (rpId is null)
            problems.Add(NotSetMessage(nameof(RelyingPartyId)));
        else if (Uri.CheckHostName(rpId) is not UriHostNameType.Dns)
            problems.Add($"{nameof(PasskeyOptions)}.{nameof(RelyingPartyId)} '{rpId}' must be a domain name, such as 'example.com'.");
        else
            isValidRpId = true;

        if (RelyingPartyName is null)
            problems.Add(NotSetMessage(nameof(RelyingPartyName)));

        if (AllowedOrigins.Count is 0 && !AllowLocalhostOrigins)
            problems.Add($"At least one origin is required in {nameof(PasskeyOptions)}.{nameof(AllowedOrigins)}.");

        foreach (string origin in AllowedOrigins)
        {
            if (!PasskeyOptionsValidator.IsValidOrigin(origin, out var uri))
            {
                problems.Add(
                    $"Allowed origin '{origin}' must be an origin such as 'https://app.example.com' (with no path) or an '{AndroidOriginPrefix}' origin.");
            }
            else if (isValidRpId && uri is not null && !PasskeyOptionsValidator.IsOnRelyingPartyId(uri.Host, rpId!))
            {
                problems.Add(
                    $"Allowed origin '{origin}' is not on the relying party ID '{rpId}' or a subdomain of it, so browsers would not run ceremonies on it.");
            }
        }

        if (CeremonyLifetime < CeremonyTimeout)
            problems.Add($"{nameof(PasskeyOptions)}.{nameof(CeremonyLifetime)} must be at least {nameof(CeremonyTimeout)}.");

        return problems;
    }

    private static string NotSetMessage(string property) => $"{nameof(PasskeyOptions)}.{property} is not set. It is required, and has no default.";

    private static void CheckRequirement(PasskeyRequirement value)
    {
        if (value is not (PasskeyRequirement.Required or PasskeyRequirement.Preferred or PasskeyRequirement.Discouraged))
            throw new ArgumentOutOfRangeException(nameof(value), value, $"The value is not a value of {nameof(PasskeyRequirement)}.");
    }

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(PasskeyOptions)} are in use, so they can no longer be changed.");
    }
}
