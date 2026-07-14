using System.Globalization;
using System.Net;

namespace Singulink.Net.Http.Api;

internal readonly struct ResponseExceptionInfo
{
    private ResponseExceptionInfo(int statusCode, string errorCode, string message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Message = message;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
    public string Message { get; }

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

    public static ResponseExceptionInfo Parse(ReadOnlySpan<char> enumerationContent)
    {
        if (!TryParse(enumerationContent, out var result))
        {
            static void Throw(ReadOnlySpan<char> content) => throw new FormatException($"Invalid enumeration ResponseExceptionInfo content format: '{content}'");
            Throw(enumerationContent);
        }

        return result;
    }

    public static ResponseExceptionInfo Parse(int statusCode, ReadOnlySpan<char> responseContent)
    {
        if (!TryParse(statusCode, responseContent, out var result))
        {
            static void Throw(int status, ReadOnlySpan<char> content) => throw new FormatException($"Invalid response ResponseExceptionInfo content format for status code {status} ({(HttpStatusCode)status}): '{content}'");
            Throw(statusCode, responseContent);
        }

        return result;
    }

    public static bool TryParse(ReadOnlySpan<char> enumerationContent, out ResponseExceptionInfo result)
    {
        result = default;

        // Check if we have a valid status code at the start of the string
        if (enumerationContent is [>= '1' and <= '5', >= '0' and <= '9', >= '0' and <= '9', ' ', .. { } rest])
        {
            return TryParse(int.Parse(enumerationContent[..3], NumberStyles.None, CultureInfo.InvariantCulture), rest, out result);
        }
        else
        {
            return false;
        }
    }

    public static bool TryParse(int statusCode, ReadOnlySpan<char> responseContent, out ResponseExceptionInfo result)
    {
        result = default;

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
            // Invalid format, return false
            return false;
        }

        // If we got here, then we have a valid status code and message, so we can create the result
        result = new ResponseExceptionInfo(statusCode, errorCode, message);
        return true;
    }

    public string ToEnumerationString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{StatusCode} [{ErrorCode}] {Message}");
    }

    public string ToResponseString()
    {
        return $"[{ErrorCode}] {Message}";
    }

    public void Throw(string rawContent, string? errorContentType = "text/singulink-response-exception-info-v1")
    {
        if ((HttpStatusCode)StatusCode is >= HttpStatusCode.OK and <= (HttpStatusCode)299)
            return;

        string? errorMessage = null;
        ApiErrorContent? errorContent = null;

        if (errorContentType is "text/singulink-response-exception-info-v1")
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
}
