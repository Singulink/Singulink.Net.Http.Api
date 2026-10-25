using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Maps the exceptions that ASP.NET Core throws for requests it cannot read to API exceptions, so that clients receive them in the toolkit's error format.
/// </summary>
/// <remarks>
/// The framework's messages name the CLR types of parameters and properties, which are internal to the service. Outside the Development environment the
/// messages reported to clients are general ones for the status, with the location that a <see cref="JsonException"/> gives and the request's content
/// type. In the Development environment they are the framework's messages.
/// </remarks>
internal static class BadRequestMapping
{
    /// <summary>
    /// Gets the API exception for a bad request exception, with its status code (such as 400, 413 or 415), the message for the client, and the original
    /// exception as the inner exception.
    /// </summary>
    public static ApiException ToApiException(HttpContext context, BadHttpRequestException exception, bool isDevelopment)
    {
        string message = isDevelopment ? GetDevelopmentMessage(exception) : GetMessage(context, exception);

        return exception.StatusCode switch {
            StatusCodes.Status400BadRequest => new BadRequestApiException(message, exception),
            StatusCodes.Status413PayloadTooLarge => new ContentTooLargeApiException(message, exception),
            StatusCodes.Status415UnsupportedMediaType => new UnsupportedMediaTypeApiException(message, exception),

            // Any other status, such as 408 for a request body that arrives too slowly, was chosen by ASP.NET Core rather than the toolkit, so it is passed
            // through as is.
            _ => new ApiException((HttpStatusCode)exception.StatusCode, message, exception),
        };
    }

    /// <summary>
    /// Gets the API exception for an error response that ASP.NET Core wrote without a body or an exception, or <see langword="null"/> if the response is
    /// not one of them: a request body that was too large (minimal APIs) or a content type that the endpoint does not accept (routing).
    /// </summary>
    public static ApiException? FromEmptyResponse(HttpContext context)
    {
        var response = context.Response;

        if (response.HasStarted || response.ContentType is not null || response.ContentLength is not (null or 0))
            return null;

        return response.StatusCode switch {
            StatusCodes.Status413PayloadTooLarge => new ContentTooLargeApiException(GetGeneralMessage(response.StatusCode)),
            StatusCodes.Status415UnsupportedMediaType => new UnsupportedMediaTypeApiException(GetUnsupportedMediaTypeMessage(context)),
            _ => null,
        };
    }

    private static string GetDevelopmentMessage(BadHttpRequestException exception)
    {
        return exception.InnerException is JsonException jsonException ? $"{exception.Message} {jsonException.Message}" : exception.Message;
    }

    private static string GetMessage(HttpContext context, BadHttpRequestException exception)
    {
        if (exception.StatusCode is StatusCodes.Status415UnsupportedMediaType)
            return GetUnsupportedMediaTypeMessage(context);

        string message = GetGeneralMessage(exception.StatusCode);

        if (exception.InnerException is not JsonException jsonException)
            return message;

        List<string> location = [];

        if (jsonException.Path is { } path)
            location.Add($"Path: {path}");

        if (jsonException.LineNumber is { } lineNumber)
            location.Add(string.Create(CultureInfo.InvariantCulture, $"LineNumber: {lineNumber}"));

        if (jsonException.BytePositionInLine is { } bytePosition)
            location.Add(string.Create(CultureInfo.InvariantCulture, $"BytePositionInLine: {bytePosition}"));

        return location.Count is 0 ? message : $"{message} {string.Join(" | ", location)}.";
    }

    private static string GetGeneralMessage(int statusCode) => statusCode switch {
        StatusCodes.Status408RequestTimeout => "The request was not received in time.",
        StatusCodes.Status413PayloadTooLarge => "The request is too large.",
        _ => "The request is not valid.",
    };

    private static string GetUnsupportedMediaTypeMessage(HttpContext context)
    {
        string? contentType = context.Request.ContentType;

        return string.IsNullOrEmpty(contentType) ?
            "The request has no content type." :
            $"The request content type \"{contentType}\" is not supported.";
    }
}
