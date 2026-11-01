namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The result of a web authentication session run by <see cref="WebAuthenticationNative"/>: how it ended, the address the page navigated to if it
/// succeeded, and a description of the error if it failed, which isn't localized or written for the person using the app.
/// </summary>
internal readonly record struct WebAuthenticationNativeResult(WebAuthenticationNativeStatus Status, string? CallbackUri = null, string? ErrorMessage = null);
