#if !NET

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Provides <see cref="OperatingSystem"/> members that .NET Standard 2.0 lacks, so the same source compiles for both builds.
/// </summary>
internal static class OperatingSystemPolyfills
{
    extension(OperatingSystem)
    {
        /// <summary>
        /// Returns <see langword="false"/>: browser apps always use the .NET build of this library, so the .NET Standard build never runs in a browser.
        /// </summary>
        public static bool IsBrowser() => false;
    }
}

#endif
