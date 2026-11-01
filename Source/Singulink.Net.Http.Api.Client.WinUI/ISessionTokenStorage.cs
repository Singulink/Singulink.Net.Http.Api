#if !__WASM__ && !WINDOWS

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Keeps the token of a <see cref="SessionTokenStore"/> under its name. Replaced by tests.
/// </summary>
internal interface ISessionTokenStorage
{
    /// <summary>
    /// Reads the stored token, or returns <see langword="null"/> if there is none.
    /// </summary>
    string? Read();

    /// <summary>
    /// Stores the token, or removes the stored one if it is <see langword="null"/>.
    /// </summary>
    void Write(string? token);
}

#endif
