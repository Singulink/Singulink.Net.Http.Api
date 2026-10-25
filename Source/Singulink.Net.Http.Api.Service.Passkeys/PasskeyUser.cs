using System.Text;

namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Represents the user a passkey is created for.
/// </summary>
public sealed class PasskeyUser
{
    /// <summary>
    /// The maximum length of a user ID in UTF-8 bytes.
    /// </summary>
    public const int MaxIdLength = 64;

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyUser"/> class.
    /// </summary>
    /// <param name="id">The user ID, which becomes the passkey's WebAuthn user handle: authenticators store it with the passkey and return it when signing
    /// in. It must not contain personal information such as an email address or user name (the WebAuthn specification forbids it), must never change or be
    /// reused for another user, and must be 1 to <see cref="MaxIdLength"/> bytes in UTF-8. IDs are compared exactly, as strings and as UTF-8 bytes, so a
    /// non-string ID formatted another way, such as with another culture, is another user ID.</param>
    /// <param name="name">The account name that passkey lists show to tell accounts apart, such as an email address or user name.</param>
    /// <param name="displayName">The user's name for display, such as "Alex Smith", or the account name if the user has no name.</param>
    public PasskeyUser(string id, string name, string displayName)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        Id = id;
        Name = name;
        DisplayName = displayName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyUser"/> class for the user of a session.
    /// </summary>
    /// <param name="user">The session token of the user, whose <see cref="ISessionToken.UserId"/> is the user ID (see
    /// <see cref="PasskeyUser(string, string, string)"/>).</param>
    /// <param name="name">The account name that passkey lists show to tell accounts apart, such as an email address or user name.</param>
    /// <param name="displayName">The user's name for display, such as "Alex Smith", or the account name if the user has no name.</param>
    public PasskeyUser(ISessionToken user, string name, string displayName) : this(user.UserId, name, displayName)
    {
    }

    /// <summary>
    /// Gets the user ID, which becomes the passkey's WebAuthn user handle.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the account name, such as an email address or user name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the user's name for display.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the WebAuthn user handle for a user ID: the ID in UTF-8.
    /// </summary>
    internal static byte[] GetUserHandle(string userId) => Encoding.UTF8.GetBytes(userId);

    internal static void ValidateId(string id, string? paramName = "id")
    {
        if (id.Length is 0 || Encoding.UTF8.GetByteCount(id) > MaxIdLength)
            throw new ArgumentException($"User IDs must be 1 to {MaxIdLength} bytes long in UTF-8.", paramName);
    }
}
