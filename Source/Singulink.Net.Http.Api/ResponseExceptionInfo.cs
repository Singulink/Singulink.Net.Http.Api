using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace Singulink.Net.Http.Api;

internal readonly struct ResponseExceptionInfo
{
    /// <summary>
    /// The name of the response header that carries the reference ID of an error response.
    /// </summary>
    public const string ReferenceIdHeaderName = "Reference-Id";

    private ResponseExceptionInfo(int statusCode, string errorCode, string message, Guid? referenceId = null, TimeSpan? retryAfter = null)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Message = message;
        ReferenceId = referenceId;
        RetryAfter = retryAfter;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
    public string Message { get; }
    public Guid? ReferenceId { get; }
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Gets the content type of the error in the toolkit's error format: plain text in UTF-8, with the <c>format=error-code</c> parameter if the error has
    /// a code.
    /// </summary>
    public string ContentType => ErrorCode.Length > 0 ? (PlainTextUtf8ContentType + "; format=error-code") : PlainTextUtf8ContentType;

    /// <summary>
    /// Gets <see cref="RetryAfter"/> in whole seconds, rounded up, as the <c>Retry-After</c> header, stream error records and hub errors carry it, or
    /// <see langword="null"/> if it is not set.
    /// </summary>
    public int? RetryAfterSeconds => RetryAfter is { } retryAfter ? GetRetryAfterSeconds(retryAfter) : null;

    private const string PlainTextMimeType = "text/plain";
    private const string PlainTextUtf8ContentType = PlainTextMimeType + "; charset=utf-8";

    // The reference ID and the retry time of a hub error follow the bracketed error code, in this order and each only if it is set, as "[ref:{GUID}] "
    // and "[retry:{seconds}] ".
    private const string HubReferenceIdPrefix = "[ref:";
    private const string HubRetryAfterPrefix = "[retry:";

    /// <summary>
    /// Creates a new <see cref="ResponseExceptionInfo" /> instance from an <see cref="ApiException" />.
    /// </summary>
    /// <param name="exception">The exception to create the <see cref="ResponseExceptionInfo" /> from.</param>
    public static ResponseExceptionInfo FromApiException(ApiException exception)
    {
        int statusCode = (int)exception.StatusCode;
        string errorCode = exception.ErrorCode ?? string.Empty;
        string message = exception.Message;
        return new(statusCode, errorCode, message, exception.ReferenceId, exception.RetryAfter);
    }

    /// <summary>
    /// Gets a retry time in whole seconds, rounded up, and at most <see cref="int.MaxValue"/>, the most that .NET reads from a <c>Retry-After</c> header.
    /// </summary>
    public static int GetRetryAfterSeconds(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero)
            return 0;

        double seconds = Math.Ceiling(retryAfter.TotalSeconds);
        return seconds < int.MaxValue ? (int)seconds : int.MaxValue;
    }

    /// <summary>
    /// Gets the retry time of a number of seconds, or <see langword="null"/> if the number is negative or longer than <see cref="TimeSpan"/> can hold.
    /// </summary>
    public static TimeSpan? GetRetryAfter(long seconds)
    {
        return seconds is >= 0 && seconds <= TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond ? TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond) : null;
    }

    /// <summary>
    /// Creates a new <see cref="ResponseExceptionInfo" /> instance from an <see cref="Exception" />.
    /// </summary>
    /// <param name="exception">The exception to create the <see cref="ResponseExceptionInfo" /> from.</param>
    /// <param name="developmentMode">Whether to include detailed exception information for development purposes.</param>
    public static ResponseExceptionInfo FromException(Exception exception, bool developmentMode = false)
    {
        if (exception is ApiException ae)
        {
            return FromApiException(ae);
        }
        else if (developmentMode)
        {
            int statusCode = 500;
            string errorCode = string.Empty;
            string message = $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";
            return new ResponseExceptionInfo(statusCode, errorCode, message);
        }
        else
        {
            string errorCode = string.Empty;
            int statusCode = 500;
            string message = string.Empty;
            return new ResponseExceptionInfo(statusCode, errorCode, message);
        }
    }

    /// <summary>
    /// Throws the <see cref="ApiException"/> that corresponds to a streaming response error record. Returns without throwing if the record does not
    /// represent an error status. A record whose code isn't a valid error code is not in the toolkit's format: like such an error response, it gets the
    /// exception for its status with a message that names the status, and its message as the error content. The exception for a <c>412</c> carries the
    /// specified user ID precondition of the request, if any (see <see cref="UserChangedApiException.ExpectedUserId"/>).
    /// </summary>
    public static void ThrowStreamError(StreamErrorInfo error, string? expectedUserId = null)
    {
        if (CreateStreamError(error, expectedUserId) is { } exception)
            throw exception;
    }

    /// <summary>
    /// Creates the <see cref="ApiException"/> that corresponds to a streaming response error record (see <see cref="ThrowStreamError"/>), or returns
    /// <see langword="null"/> if the record does not represent an error status.
    /// </summary>
    public static ApiException? CreateStreamError(StreamErrorInfo error, string? expectedUserId = null)
    {
        string message = error.Message ?? string.Empty;
        string code = error.Code ?? string.Empty;
        var referenceId = Guid.TryParse(error.ReferenceId, out var id) ? id : (Guid?)null;
        var retryAfter = error.RetryAfter is { } seconds ? GetRetryAfter(seconds) : null;

        if (ApiException.ContainsInvalidErrorCodeChars(code.AsSpan()))
        {
            if ((HttpStatusCode)error.Status is not (>= HttpStatusCode.OK and <= (HttpStatusCode)299))
            {
                var errorContent = new ApiErrorContent(message, PlainTextMimeType);
                return CreateForStatus(
                    error.Status, GetDefaultMessage(error.Status), string.Empty, errorContent, retryAfter, referenceId, expectedUserId: expectedUserId);
            }

            return null;
        }

        return new ResponseExceptionInfo(error.Status, code, message, referenceId).CreateException(message, PlainTextMimeType, retryAfter, expectedUserId);
    }

    /// <summary>
    /// Throws the <see cref="ApiException"/> that corresponds to an error response. Returns without throwing if the status is a success status.
    /// </summary>
    /// <param name="statusCode">The status code of the response.</param>
    /// <param name="responseContent">The content of the response.</param>
    /// <param name="mimeType">The media type of the response content, if any.</param>
    /// <param name="hasErrorCode">Whether the content type has the <c>format=error-code</c> parameter.</param>
    /// <param name="retryAfter">The time from the response's <c>Retry-After</c> header, if any.</param>
    /// <param name="location">The absolute address from the response's <c>Location</c> header, if any. The exception for a <c>3xx</c> response carries it,
    /// and a redirect's message names its scheme, host, port and path.</param>
    /// <param name="referenceId">The reference ID from the response's <c>Reference-Id</c> header (<see cref="ReferenceIdHeaderName"/>), if any, which the
    /// exception for an error response carries.</param>
    /// <param name="expectedUserId">The user ID precondition of the request, if any, which the exception for a <c>412</c> response carries (see
    /// <see cref="UserChangedApiException.ExpectedUserId"/>).</param>
    public static void ParseAndThrow(
        int statusCode,
        string responseContent,
        string? mimeType,
        bool hasErrorCode,
        TimeSpan? retryAfter,
        Uri? location = null,
        Guid? referenceId = null,
        string? expectedUserId = null)
    {
        if (statusCode is >= 300 and <= 399)
        {
            // Redirects are not followed, so a 3xx response is reported with its status like an error response that is not in the toolkit's format. The
            // message leaves out the parts of the address that can carry secrets, such as user info and a signed query.
            string message = location is null || statusCode is 304 ? $"The service responded with {GetStatusText(statusCode)}." :
                $"The service redirected the request to {location.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)} " +
                $"({GetStatusText(statusCode)}), and redirects are not followed.";

            throw new ApiException((HttpStatusCode)statusCode, message) {
                ErrorContent = new ApiErrorContent(responseContent, mimeType ?? "unknown"),
                RetryAfter = retryAfter,
                Location = location,
            };
        }

        if (TryParseImpl(statusCode, responseContent.AsSpan(), responseContent, mimeType ?? "unknown", hasErrorCode, out var info))
        {
            new ResponseExceptionInfo(info.StatusCode, info.ErrorCode, info.Message, referenceId).Throw(responseContent, mimeType, retryAfter, expectedUserId);
        }
        else if ((HttpStatusCode)statusCode is not (>= HttpStatusCode.OK and <= (HttpStatusCode)299))
        {
            ThrowInvalid(statusCode, responseContent, mimeType, retryAfter, referenceId, expectedUserId);
        }

        static void ThrowInvalid(int statusCode, string content, string? mimeType, TimeSpan? retryAfter, Guid? referenceId, string? expectedUserId)
        {
            // The response is not in the toolkit's format (such as a proxy's error page), so only its status selects the exception.
            var errorContent = new ApiErrorContent(content, mimeType ?? "unknown");
            throw CreateForStatus(
                statusCode, GetDefaultMessage(statusCode), string.Empty, errorContent, retryAfter, referenceId, expectedUserId: expectedUserId);
        }
    }

    /// <summary>
    /// Creates the <see cref="ApiException"/> that corresponds to a hub error message produced by the API exception hub filter, or returns
    /// <see langword="null"/> if the message is not in that format. The exception for a <c>412</c> carries the specified user ID precondition of the hub
    /// connection, if any (see <see cref="UserChangedApiException.ExpectedUserId"/>).
    /// </summary>
    public static ApiException? TryCreateFromHubError(string message, string? expectedUserId = null)
    {
        const string hubExceptionPrefix = "HubException: ";

        // SignalR prefixes error messages with a description and the exception type name, e.g. "An unexpected error occurred invoking 'X' on the
        // server. HubException: 403 [code] message".

        var span = message.AsSpan();
        int prefixIndex = span.IndexOf(hubExceptionPrefix, StringComparison.Ordinal);

        if (prefixIndex >= 0)
            span = span[(prefixIndex + hubExceptionPrefix.Length)..];

        if (span is not [>= '1' and <= '5', >= '0' and <= '9', >= '0' and <= '9', ' ', .. var rest])
            return null;

#if NET
        int statusCode = int.Parse(span[..3], NumberStyles.None, CultureInfo.InvariantCulture);
#else
        int statusCode = int.Parse(span[..3].ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
#endif
        string content = rest.ToString();

        if (!TryParseImpl(statusCode, rest, content, PlainTextMimeType, hasErrorCode: true, out var info))
            return null;

        // A reference ID and then a retry time follow the bracketed error code, each if there is one. A marker whose value is not valid is part of the
        // message.
        string hubMessage = info.Message;
        Guid? referenceId = null;
        TimeSpan? retryAfter = null;

        if (TryReadHubMarker(hubMessage, HubReferenceIdPrefix, out string? referenceIdValue, out string remainder) &&
            Guid.TryParseExact(referenceIdValue, "D", out var id))
        {
            referenceId = id;
            hubMessage = remainder;
        }

        if (TryReadHubMarker(hubMessage, HubRetryAfterPrefix, out string? retryAfterValue, out remainder) &&
            long.TryParse(retryAfterValue, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) && GetRetryAfter(seconds) is { } time)
        {
            retryAfter = time;
            hubMessage = remainder;
        }

        var hubInfo = new ResponseExceptionInfo(info.StatusCode, info.ErrorCode, hubMessage, referenceId);
        return hubInfo.CreateException(hubMessage, PlainTextMimeType, retryAfter, expectedUserId);

        static bool TryReadHubMarker(string message, string prefix, [NotNullWhen(true)] out string? value, out string remainder)
        {
            int end = message.StartsWith(prefix, StringComparison.Ordinal) ? message.IndexOf(']', prefix.Length) : -1;

            if (end < 0)
            {
                value = null;
                remainder = message;
                return false;
            }

            value = message.Substring(prefix.Length, end - prefix.Length);
            remainder = message.Substring(end + 1).TrimStart();
            return true;
        }
    }

    /// <summary>
    /// Formats the error for transport as a hub exception message (parsed by <see cref="TryCreateFromHubError"/>): the status, the bracketed error code,
    /// the reference ID as <c>[ref:{GUID}]</c> if there is one, the retry time in whole seconds, rounded up, as <c>[retry:{seconds}]</c> if there is one,
    /// and the message.
    /// </summary>
    public string ToHubErrorString()
    {
        string referenceId = ReferenceId is { } id ? $"{HubReferenceIdPrefix}{id:D}] " : string.Empty;
        string retryAfter = RetryAfterSeconds is { } seconds ? HubRetryAfterPrefix + seconds.ToString(CultureInfo.InvariantCulture) + "] " : string.Empty;

#if NET
        return string.Create(CultureInfo.InvariantCulture, $"{StatusCode} [{ErrorCode}] {referenceId}{retryAfter}{Message}");
#else
        return FormattableString.Invariant($"{StatusCode} [{ErrorCode}] {referenceId}{retryAfter}{Message}");
#endif
    }

    private void Throw(string rawContent, string? errorContentType, TimeSpan? retryAfter, string? expectedUserId)
    {
        if (CreateException(rawContent, errorContentType, retryAfter, expectedUserId) is { } exception)
            throw exception;
    }

    private ApiException? CreateException(string rawContent, string? errorContentType, TimeSpan? retryAfter, string? expectedUserId)
    {
        if ((HttpStatusCode)StatusCode is >= HttpStatusCode.OK and <= (HttpStatusCode)299)
            return null;

        string errorMessage = string.Empty;
        ApiErrorContent? errorContent = null;

        if (errorContentType is PlainTextMimeType)
            errorMessage = Message;
        else if (!string.IsNullOrWhiteSpace(Message))
            errorContent = new(rawContent, errorContentType ?? "unknown");

        if (string.IsNullOrWhiteSpace(errorMessage))
            errorMessage = GetDefaultMessage(StatusCode);

        return CreateForStatus(StatusCode, errorMessage, ErrorCode, errorContent, retryAfter, ReferenceId, expectedUserId: expectedUserId);
    }

    /// <summary>
    /// Gets the message for an error without a message of its own, such as <c>"The service returned an error (502 Bad Gateway)."</c>.
    /// </summary>
    private static string GetDefaultMessage(int statusCode) => $"The service returned an error ({GetStatusText(statusCode)}).";

    /// <summary>
    /// Gets the status code followed by its reason phrase, if it is a standard one, such as <c>"502 Bad Gateway"</c>.
    /// </summary>
    private static string GetStatusText(int statusCode)
    {
        string status = statusCode.ToString(CultureInfo.InvariantCulture);
        return GetReasonPhrase(statusCode) is { } reasonPhrase ? status + " " + reasonPhrase : status;
    }

    private static string? GetReasonPhrase(int statusCode) => statusCode switch {
        300 => "Multiple Choices",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        304 => "Not Modified",
        305 => "Use Proxy",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        402 => "Payment Required",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        407 => "Proxy Authentication Required",
        408 => "Request Timeout",
        409 => "Conflict",
        410 => "Gone",
        411 => "Length Required",
        412 => "Precondition Failed",
        413 => "Content Too Large",
        414 => "URI Too Long",
        415 => "Unsupported Media Type",
        416 => "Range Not Satisfiable",
        417 => "Expectation Failed",
        421 => "Misdirected Request",
        422 => "Unprocessable Content",
        426 => "Upgrade Required",
        428 => "Precondition Required",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        451 => "Unavailable For Legal Reasons",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        505 => "HTTP Version Not Supported",
        _ => null,
    };

    /// <summary>
    /// Creates the toolkit's exception for a status code, with the specified values. Only the exception for a <c>412</c> carries the expected user ID (see
    /// <see cref="UserChangedApiException.ExpectedUserId"/>).
    /// </summary>
    internal static ApiException CreateForStatus(
        int statusCode,
        string message,
        string code,
        ApiErrorContent? content,
        TimeSpan? retryAfter,
        Guid? referenceId,
        Exception? innerException = null,
        Uri? location = null,
        string? expectedUserId = null)
    {
        return statusCode switch
        {
            400 => new BadRequestApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            401 => new UnauthorizedApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            403 => new ForbiddenApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            404 => new NotFoundApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            409 => new ConflictApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            412 => new UserChangedApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
                ExpectedUserId = expectedUserId,
            },
            413 => new ContentTooLargeApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            415 => new UnsupportedMediaTypeApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            422 => new ValidationApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            428 => new UserRequiredApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            429 => new TooManyRequestsApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            500 => new ServerErrorApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            503 => new ServiceUnavailableApiException(message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
            _ => new ApiException((HttpStatusCode)statusCode, message, innerException) {
                ErrorCode = code, ErrorContent = content, RetryAfter = retryAfter, ReferenceId = referenceId, Location = location,
            },
        };
    }

    private static bool TryParseImpl(
        int statusCode, ReadOnlySpan<char> responseContent, string originalResponseContent, string mimeType, bool hasErrorCode, out ResponseExceptionInfo info)
    {
        // Content of another type is not in the toolkit's format.
        if (mimeType != PlainTextMimeType)
        {
            info = default;
            return false;
        }

        // Without the error code format, the whole content is the message.
        if (!hasErrorCode)
        {
            info = new(statusCode, string.Empty, originalResponseContent);
            return true;
        }

        // The content is the error code in brackets, followed by the message. Content whose bracketed text isn't a valid error code is not in the toolkit's
        // format.
        string errorCode = string.Empty;
        string message;

        if (responseContent is ['[', .. var rest] && rest.IndexOf(']') is >= 0 and { } endBracketIndex &&
            !ApiException.ContainsInvalidErrorCodeChars(rest[..endBracketIndex]))
        {
            if (endBracketIndex > 0)
                errorCode = rest[..endBracketIndex].ToString();

            message = rest[(endBracketIndex + 1)..].TrimStart().ToString();
        }
        else
        {
            info = default;
            return false;
        }

        info = new ResponseExceptionInfo(statusCode, errorCode, message);
        return true;
    }

    public string ToResponseString()
    {
        if (string.IsNullOrEmpty(ErrorCode))
        {
            return Message;
        }
        else
        {
            return $"[{ErrorCode}] {Message}";
        }
    }
}
