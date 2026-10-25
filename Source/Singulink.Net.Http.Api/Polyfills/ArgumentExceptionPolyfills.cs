#if !NET

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Singulink.Net.Http.Api;

/// <summary>
/// Provides <see cref="ArgumentException"/> members that .NET Standard 2.0 lacks, so the same source compiles for both builds.
/// </summary>
internal static class ArgumentExceptionPolyfills
{
    extension(ArgumentException)
    {
        /// <summary>
        /// Throws an exception if <paramref name="argument"/> is <see langword="null"/> or empty.
        /// </summary>
        public static void ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null || argument.Length is 0)
                ThrowNullOrEmptyException(argument, paramName);
        }
    }

    [DoesNotReturn]
    private static void ThrowNullOrEmptyException(string? argument, string? paramName)
    {
        if (argument is null)
            throw new ArgumentNullException(paramName);

        throw new ArgumentException("The value cannot be an empty string.", paramName);
    }
}

#endif
