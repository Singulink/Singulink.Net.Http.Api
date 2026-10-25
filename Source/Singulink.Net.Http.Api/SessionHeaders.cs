namespace Singulink.Net.Http.Api;

/// <summary>
/// Contains the name and values of the response header that the session handling of the <c>Singulink.Net.Http.Api.Service</c> package marks a sign-in's
/// and a sign-out's response with.
/// </summary>
internal static class SessionHeaders
{
    /// <summary>
    /// The header that marks the response of a sign-in (<see cref="SignIn"/>) or a sign-out (<see cref="SignOut"/>). The toolkit's native clients take the
    /// session cookie that such a response sets or deletes whatever session the device has by then, and that of any other response only if its request
    /// sent the current session token, and count a sign-in's response as the start of a session. In the browser, which applies cookies itself, the client
    /// doesn't read it. The clients take only these two values, ignoring case, and take a response whose header has another value as an unmarked one.
    /// </summary>
    public const string Change = "Session-Change";

    /// <summary>
    /// The value of <see cref="Change"/> on a sign-in's response.
    /// </summary>
    public const string SignIn = "sign-in";

    /// <summary>
    /// The value of <see cref="Change"/> on a sign-out's response.
    /// </summary>
    public const string SignOut = "sign-out";

    /// <summary>
    /// Gets a value indicating whether a value of <see cref="Change"/> is one that the clients take: <see cref="SignIn"/> or <see cref="SignOut"/>.
    /// </summary>
    public static bool IsChangeValue(string value)
    {
        value = value.Trim();
        return string.Equals(value, SignIn, StringComparison.OrdinalIgnoreCase) || string.Equals(value, SignOut, StringComparison.OrdinalIgnoreCase);
    }
}
