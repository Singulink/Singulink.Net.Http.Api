using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace Singulink.Net.Http.Api;

internal readonly struct ResponseExceptionInfo
{
    private ResponseExceptionInfo(int statusCode, string errorCode, string message, string? mimeType = null)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Message = message;

        if (mimeType != null)
        {
            // Ensure we see a normalized MIME type.

            mimeType = mimeType.ToLowerInvariant();

            if (mimeType.Contains(';') && mimeType.Any(char.IsWhiteSpace))
                mimeType = string.Join(";", mimeType.Split(';').Select((x) => x.Trim()));
        }

        MimeType = mimeType ?? (errorCode.Length > 0 ? ResponseExceptionInfoMimeType : PlainTextMimeType);
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
    public string Message { get; }
    public string MimeType { get; } // Only used for non-enumeration format.

    private const string PlainTextMimeType = "text/plain";
    private const string ResponseExceptionInfoMimeType = "text/plain;format=error-code";

    /// <summary>
    /// Creates a new <see cref="ResponseExceptionInfo" /> instance from an <see cref="ApiException" />.
    /// </summary>
    /// <param name="exception">The exception to create the <see cref="ResponseExceptionInfo" /> from.</param>
    public static ResponseExceptionInfo FromApiException(ApiException exception)
    {
        int statusCode = (int)exception.StatusCode;
        string errorCode = exception.ErrorCode ?? string.Empty;
        string message = exception.Message;
        return new(statusCode, errorCode, message);
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

    public static void ParseAndThrow(string enumerationContent)
    {
        // Check if we have a valid status code at the start of the string
        if (enumerationContent.AsSpan() is [>= '1' and <= '5', >= '0' and <= '9', >= '0' and <= '9', ' ', .. var rest])
        {
            if (TryParseImpl(int.Parse(enumerationContent[..3], NumberStyles.None, CultureInfo.InvariantCulture), rest, enumerationContent, ResponseExceptionInfoMimeType, out var info))
            {
                info.Throw(enumerationContent, ResponseExceptionInfoMimeType);
            }
            else
            {
                ThrowInvalid(enumerationContent);
            }
        }
        else
        {
            ThrowInvalid(enumerationContent);
        }

        static void ThrowInvalid(string content)
        {
            throw new ApiException(HttpStatusCode.InternalServerError, $"Invalid enumeration exception content: {content}");
        }
    }

    public static void ParseAndThrow(int statusCode, string responseContent, string? mimeType)
    {
        if (TryParseImpl(statusCode, responseContent.AsSpan(), responseContent, mimeType ?? "unknown", out var info))
        {
            info.Throw(responseContent, mimeType);
        }
        else if ((HttpStatusCode)statusCode is not (>= HttpStatusCode.OK and <= (HttpStatusCode)299))
        {
            ThrowInvalid(statusCode, responseContent, mimeType);
        }

        static void ThrowInvalid(int statusCode, string content, string? mimeType)
        {
            throw new ApiException(HttpStatusCode.InternalServerError, $"Unknown service error ({statusCode})")
            {
                ErrorContent = new ApiErrorContent(content, mimeType ?? "unknown"),
            };
        }
    }

    private void Throw(string rawContent, string? errorContentType)
    {
        if ((HttpStatusCode)StatusCode is >= HttpStatusCode.OK and <= (HttpStatusCode)299)
            return;

        string? errorMessage = null;
        ApiErrorContent? errorContent = null;

        if (errorContentType is ResponseExceptionInfoMimeType or PlainTextMimeType)
            errorMessage = Message;
        else if (!string.IsNullOrWhiteSpace(Message))
            errorContent = new(rawContent, errorContentType ?? "unknown");

        if (string.IsNullOrWhiteSpace(errorMessage))
            errorMessage = $"Unknown service error ({(HttpStatusCode)StatusCode}).";

        // NOTE: must be kept in sync with the list of recognized status codes in IsRecognizedStatusCode
        var ex = (HttpStatusCode)StatusCode switch
        {
            HttpStatusCode.BadRequest => new BadRequestApiException(errorMessage) { ErrorContent = errorContent, ErrorCode = ErrorCode },
            HttpStatusCode.Unauthorized => new UnauthorizedApiException(errorMessage) { ErrorContent = errorContent, ErrorCode = ErrorCode },
            HttpStatusCode.Forbidden => new ForbiddenApiException(errorMessage) { ErrorContent = errorContent, ErrorCode = ErrorCode },
            HttpStatusCode.NotFound => new NotFoundApiException(errorMessage) { ErrorContent = errorContent, ErrorCode = ErrorCode },
            HttpStatusCode.PreconditionFailed => new UserChangedApiException(errorMessage) { ErrorContent = errorContent, ErrorCode = ErrorCode },
            HttpStatusCode.UnprocessableEntity => new ValidationApiException(errorMessage) { ErrorContent = errorContent, ErrorCode = ErrorCode },
            HttpStatusCode.PreconditionRequired => new UserRequiredApiException(errorMessage) { ErrorContent = errorContent, ErrorCode = ErrorCode },
            _ => new ApiException((HttpStatusCode)StatusCode, errorMessage) { ErrorContent = errorContent, ErrorCode = ErrorCode },
        };

        throw ex;
    }

    private static bool TryParseImpl(int statusCode, ReadOnlySpan<char> responseContent, string originalResponseContent, string mimeType, out ResponseExceptionInfo info)
    {
        // Check plain text mime type
        if (mimeType == PlainTextMimeType)
        {
            info = new(statusCode, string.Empty, originalResponseContent, mimeType);
            return true;
        }

        // Check custom mime type
        if (mimeType != ResponseExceptionInfoMimeType)
        {
            info = default;
            return false;
        }

        // If it is a valid message, then we should have [<error-code>] <message> format, otherwise we just have <message>
        // Note: to ensure that we can round-trip, we require that no error code gets encoded as [] <message>
        string errorCode = string.Empty;
        string message;

        if (responseContent is ['[', .. var rest] && rest.IndexOf(']') is >= 0 and { } endBracketIndex)
        {
            if (endBracketIndex > 0)
                errorCode = rest[..endBracketIndex].ToString();

            message = rest[(endBracketIndex + 1)..].TrimStart().ToString();
        }
        else
        {
            // Invalid format
            info = default;
            return false;
        }

        // If we got here, then we have a valid status code and message, so we can create the result
        info = new ResponseExceptionInfo(statusCode, errorCode, message, mimeType);
        return true;
    }

    public string ToEnumerationString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{StatusCode} [{ErrorCode}] {Message}");
    }

    public string ToResponseString()
    {
        if (MimeType == PlainTextMimeType)
        {
            Debug.Assert(ErrorCode.Length == 0, "Error code should be empty for plain text response.");
            return Message;
        }
        else
        {
            Debug.Assert(MimeType == ResponseExceptionInfoMimeType, "Unexpected mime type for response exception info.");
            return $"[{ErrorCode}] {Message}";
        }
    }
}
