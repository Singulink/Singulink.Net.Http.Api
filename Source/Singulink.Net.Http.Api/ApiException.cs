using System.Buffers;
using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs during API operations.
/// </summary>
public class ApiException : Exception
{
    /// <summary>
    /// Gets the HTTP status code associated with the API exception.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets the error content associated with the API exception if the error response was not in <c>text/plain</c> or a known/expected format.
    /// </summary>
    public ApiErrorContent? ErrorContent { get; init; }

    private static readonly SearchValues<char> _validErrorCodeChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_");

    /// <summary>
    /// Gets the application-specific error code associated with the API exception, if available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must only consist of valid ASCII letters, digits, hyphens, and underscores.
    /// </para>
    /// <para>
    /// An error code of <c>""</c> is normalized to <see langword="null" />.
    /// </para>
    /// </remarks>
    public string? ErrorCode
    {
        get;
        init
        {
            if (value.AsSpan().ContainsAnyExcept(_validErrorCodeChars))
            {
                static void Throw() => throw new ArgumentException("Error code must only consist of valid ASCII letters, digits, hyphens, and underscores.", nameof(value));
                Throw();
            }

            field = value?.Length is not > 0 ? null : value;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiException"/> class with a specified HTTP status code and error message.
    /// </summary>
    public ApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiException"/> class with a specified HTTP status code, error message and inner exception.
    /// </summary>
    public ApiException(HttpStatusCode statusCode, string message, Exception? innerException) : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
