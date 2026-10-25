using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service.Testing;

/// <summary>
/// A software authenticator that holds one passkey (an ES256 key pair) and answers WebAuthn ceremonies as a browser would: it takes the options JSON of a
/// ceremony and returns the credential JSON (the <c>PublicKeyCredential.toJSON()</c> form) with a <c>none</c> attestation.
/// </summary>
/// <remarks>
/// The passkey is created by <see cref="Create"/>, which takes the user handle from the creation options, and used by <see cref="Get"/>. The
/// authenticator signs for the relying party ID in the options unless <see cref="RelyingPartyId"/> is set, and reports <see cref="Origin"/> as the origin
/// of the page in the client data.
/// </remarks>
public sealed class SoftwareAuthenticator : IDisposable
{
    private const byte UserPresentFlag = 0x01;
    private const byte UserVerifiedFlag = 0x04;
    private const byte BackupEligibleFlag = 0x08;
    private const byte BackedUpFlag = 0x10;
    private const byte AttestedCredentialDataFlag = 0x40;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// Initializes a new instance of the <see cref="SoftwareAuthenticator"/> class.
    /// </summary>
    /// <param name="origin">The origin of the page that runs the ceremonies, such as <c>https://app.example.com</c>, which the authenticator reports in the
    /// client data.</param>
    public SoftwareAuthenticator(string origin)
    {
        Origin = origin;
    }

    /// <summary>
    /// Gets or sets the origin of the page that runs the ceremonies, which the authenticator reports in the client data.
    /// </summary>
    public string Origin { get; set; }

    /// <summary>
    /// Gets or sets the relying party ID the authenticator signs for, or <see langword="null"/> for the one in the options of each ceremony. Default is
    /// <see langword="null"/>.
    /// </summary>
    public string? RelyingPartyId { get; set; }

    /// <summary>
    /// Gets the credential ID of the passkey. Default is 16 random bytes.
    /// </summary>
    public byte[] CredentialId { get; init; } = RandomNumberGenerator.GetBytes(16);

    /// <summary>
    /// Gets the AAGUID of the authenticator model that the new passkey reports. Default is <see cref="Guid.Empty"/>.
    /// </summary>
    public Guid AaGuid { get; init; }

    /// <summary>
    /// Gets or sets the signature counter that the passkey reports. If <see cref="CountsSignatures"/> is <see langword="true"/>, it is incremented before
    /// each use of the passkey. Default is 0.
    /// </summary>
    public uint SignCount { get; set; }

    /// <summary>
    /// Gets a value indicating whether the signature counter is incremented before each use of the passkey. Default is <see langword="false"/>, which
    /// many synced passkeys also do.
    /// </summary>
    public bool CountsSignatures { get; init; }

    /// <summary>
    /// Gets a value indicating whether the passkey reports that it can be backed up. Default is <see langword="true"/>.
    /// </summary>
    public bool IsBackupEligible { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the passkey reports that it is backed up. Default is <see langword="true"/>.
    /// </summary>
    public bool IsBackedUp { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the authenticator reports that it verified the user. Default is <see langword="true"/>.
    /// </summary>
    public bool VerifiesUser { get; set; } = true;

    /// <summary>
    /// Gets the transports that the new passkey reports. Default is <c>internal</c> and <c>hybrid</c>.
    /// </summary>
    public IReadOnlyList<string> Transports { get; init; } = ["internal", "hybrid"];

    /// <summary>
    /// Gets the attachment that the credentials report as the browser would. <see cref="PasskeyAttachment.Unknown"/> leaves it out. Default is
    /// <see cref="PasskeyAttachment.Platform"/>.
    /// </summary>
    public PasskeyAttachment Attachment { get; init; } = PasskeyAttachment.Platform;

    /// <summary>
    /// Gets or sets the user handle that the passkey returns when it is used, or <see langword="null"/> for none. <see cref="Create"/> sets it to the
    /// user ID in the creation options.
    /// </summary>
    public byte[]? UserHandle { get; set; }

    /// <summary>
    /// Releases the passkey's key.
    /// </summary>
    public void Dispose() => _key.Dispose();

    /// <summary>
    /// Creates the passkey for the specified creation options and returns the credential JSON of a registration ceremony.
    /// </summary>
    /// <param name="creationOptionsJson">The options of the ceremony (see <see cref="PasskeyCeremony.OptionsJson"/>).</param>
    public string Create(string creationOptionsJson)
    {
        using var options = JsonDocument.Parse(creationOptionsJson);
        var root = options.RootElement;

        string challenge = root.GetProperty("challenge").GetString()!;
        string rpId = RelyingPartyId ?? root.GetProperty("rp").GetProperty("id").GetString()!;
        UserHandle = Base64Url.DecodeFromChars(root.GetProperty("user").GetProperty("id").GetString());

        byte[] clientData = CreateClientData("webauthn.create", challenge);
        byte[] authData = CreateAuthenticatorData(rpId, AttestedCredentialDataFlag, CreateAttestedCredentialData());

        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(3);
        writer.WriteTextString("fmt");
        writer.WriteTextString("none");
        writer.WriteTextString("attStmt");
        writer.WriteStartMap(0);
        writer.WriteEndMap();
        writer.WriteTextString("authData");
        writer.WriteByteString(authData);
        writer.WriteEndMap();

        var transports = new JsonArray();

        foreach (string transport in Transports)
            transports.Add((JsonNode)transport);

        var credential = CreateCredentialJson(new JsonObject {
            ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
            ["attestationObject"] = Base64Url.EncodeToString(writer.Encode()),
            ["transports"] = transports,
        });

        credential["clientExtensionResults"] = new JsonObject { ["credProps"] = new JsonObject { ["rk"] = true } };
        return credential.ToJsonString();
    }

    /// <summary>
    /// Uses the passkey for the specified request options and returns the credential JSON of a sign-in or verification ceremony.
    /// </summary>
    /// <param name="requestOptionsJson">The options of the ceremony (see <see cref="PasskeyCeremony.OptionsJson"/>).</param>
    /// <param name="includeUserHandle">Whether the credential includes the user handle. Default is <see langword="true"/>.</param>
    public string Get(string requestOptionsJson, bool includeUserHandle = true)
    {
        using var options = JsonDocument.Parse(requestOptionsJson);
        var root = options.RootElement;

        string challenge = root.GetProperty("challenge").GetString()!;
        string rpId = RelyingPartyId ?? root.GetProperty("rpId").GetString()!;

        if (CountsSignatures)
            SignCount++;

        byte[] clientData = CreateClientData("webauthn.get", challenge);
        byte[] authData = CreateAuthenticatorData(rpId, 0, []);
        byte[] signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return CreateCredentialJson(new JsonObject {
            ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
            ["authenticatorData"] = Base64Url.EncodeToString(authData),
            ["signature"] = Base64Url.EncodeToString(signature),
            ["userHandle"] = includeUserHandle && UserHandle is not null ? Base64Url.EncodeToString(UserHandle) : null,
        }).ToJsonString();
    }

    private JsonObject CreateCredentialJson(JsonObject response)
    {
        string id = Base64Url.EncodeToString(CredentialId);

        return new JsonObject {
            ["id"] = id,
            ["rawId"] = id,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = Attachment switch {
                PasskeyAttachment.Platform => "platform",
                PasskeyAttachment.CrossPlatform => "cross-platform",
                _ => null,
            },
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = response,
        };
    }

    private byte[] CreateClientData(string type, string challenge)
    {
        var clientData = new JsonObject {
            ["type"] = type,
            ["challenge"] = challenge,
            ["origin"] = Origin,
            ["crossOrigin"] = false,
        };

        return Encoding.UTF8.GetBytes(clientData.ToJsonString());
    }

    private byte[] CreateAuthenticatorData(string rpId, byte extraFlags, byte[] attestedCredentialData)
    {
        byte flags = (byte)(UserPresentFlag | extraFlags);

        if (VerifiesUser)
            flags |= UserVerifiedFlag;

        if (IsBackupEligible)
            flags |= BackupEligibleFlag;

        if (IsBackedUp)
            flags |= BackedUpFlag;

        byte[] signCount = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(signCount, SignCount);

        return [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), flags, .. signCount, .. attestedCredentialData];
    }

    private byte[] CreateAttestedCredentialData()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);

        // COSE_Key for ES256: kty EC2 (2), alg ES256 (-7), crv P-256 (1), x, y.
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteInt32(2);
        writer.WriteInt32(3);
        writer.WriteInt32(-7);
        writer.WriteInt32(-1);
        writer.WriteInt32(1);
        writer.WriteInt32(-2);
        writer.WriteByteString(parameters.Q.X!);
        writer.WriteInt32(-3);
        writer.WriteByteString(parameters.Q.Y!);
        writer.WriteEndMap();

        byte[] credentialIdLength = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(credentialIdLength, (ushort)CredentialId.Length);

        return [.. AaGuid.ToByteArray(bigEndian: true), .. credentialIdLength, .. CredentialId, .. writer.Encode()];
    }
}
