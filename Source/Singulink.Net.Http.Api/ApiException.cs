using System.Buffers;
using System.Collections;
using System.Net;

namespace Singulink.Net.Http.Api;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// Represents an exception that occurs during API operations.
/// </summary>
public class ApiException : Exception
{
    private const string ValidErrorCodeChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";

#if NET
    private static readonly SearchValues<char> _validErrorCodeChars = SearchValues.Create(ValidErrorCodeChars);
#endif

    /// <summary>
    /// Gets the HTTP status code associated with the API exception.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets the content of the error response if it is not in the toolkit's error format (<c>text/plain</c> content), such as an error page from a proxy,
    /// and of a <c>3xx</c> response; otherwise <see langword="null"/>.
    /// </summary>
    public ApiErrorContent? ErrorContent { get; init; }

    /// <summary>
    /// Gets the application-specific error code associated with the API exception, if available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must only consist of ASCII letters, digits, hyphens and underscores.
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
            if (ContainsInvalidErrorCodeChars(value.AsSpan()))
            {
                static void Throw() =>
                    throw new ArgumentException("The error code must only consist of ASCII letters, digits, hyphens and underscores.", nameof(value));
                Throw();
            }

            field = value?.Length is not > 0 ? null : value;
        }
    }

    /// <summary>
    /// Gets how long the client should wait before making the request again, if the service specified it. Error responses carry it in the
    /// <c>Retry-After</c> header, the error records of streaming responses in their <c>retryAfter</c> property, and hub errors in their message, each in
    /// whole seconds, rounded up.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan? RetryAfter
    {
        get;
        init
        {
            if (value < TimeSpan.Zero)
            {
                static void Throw() => throw new ArgumentOutOfRangeException(nameof(value), "Retry time cannot be negative.");
                Throw();
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets the reference ID that the service reported the error with, such as the ID it logged an unexpected exception with, or <see langword="null"/> if
    /// it reported none. Error responses carry it in the <c>Reference-Id</c> header, the error records of streaming responses in their <c>referenceId</c>
    /// property, and hub errors in their message.
    /// </summary>
    public Guid? ReferenceId { get; init; }

    /// <summary>
    /// Gets the address from the <c>Location</c> header of the <c>3xx</c> response that the exception reports, resolved against the request's address if it
    /// is relative, or <see langword="null"/> if the response had no valid header or is not a <c>3xx</c> response. API clients do not follow redirects,
    /// except on browser platforms.
    /// </summary>
    public Uri? Location { get; internal init; }

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

    /// <summary>
    /// Creates a copy of the exception with another message. The copy keeps the status code, the error code, the retry time, the reference ID, the error
    /// content, the location, the expected user ID of a <see cref="UserChangedApiException"/>, the data (<see cref="Exception.Data"/>) and the inner
    /// exception. It is of the toolkit's exception type for the status code, such as <see cref="ConflictApiException"/> for <c>409</c>, also when the
    /// exception is of a type derived from it, whose own members it doesn't keep. It has no stack trace, and doesn't keep <see cref="Exception.HResult"/>,
    /// <see cref="Exception.HelpLink"/> or <see cref="Exception.Source"/>.
    /// </summary>
    /// <param name="message">The message of the copy.</param>
    public ApiException WithMessage(string message)
    {
        string? expectedUserId = (this as UserChangedApiException)?.ExpectedUserId;
        var copy = ResponseExceptionInfo.CreateForStatus(
            (int)StatusCode, message, ErrorCode ?? string.Empty, ErrorContent, RetryAfter, ReferenceId, InnerException, Location, expectedUserId);

        foreach (DictionaryEntry entry in Data)
            copy.Data[entry.Key] = entry.Value;

        return copy;
    }

    /// <summary>
    /// Gets a value indicating whether the specified error code has characters that an error code can't have (see <see cref="ErrorCode"/>).
    /// </summary>
    internal static bool ContainsInvalidErrorCodeChars(ReadOnlySpan<char> value)
    {
#if NET
        return value.ContainsAnyExcept(_validErrorCodeChars);
#else
        foreach (char c in value)
        {
            if (ValidErrorCodeChars.IndexOf(c) < 0)
                return true;
        }

        return false;
#endif
    }
}
