using System.Text.Json.Serialization;
using RuntimeNullables;

namespace Singulink.Net.Http.Api;

/// <summary>
/// Represents opaque information about an exception that occurred, which can be serialized and deserialized with JSON.
/// </summary>
[NullChecks(false)]
[JsonConverter(typeof(ResponseExceptionInfoJsonConverter))]
public sealed class ResponseExceptionInfo()
{
    private ResponseExceptionInfo(int statusCode, string message, string contentType) : this()
    {
        StatusCode = statusCode;
        Message = message;
        ContentType = contentType;
    }

    /// <summary>
    /// Opaque information about the exception that occurred, which can be serialized and deserialized with JSON.
    /// </summary>
#pragma warning disable SA1623 // Property summary documentation should match accessors
    public int StatusCode { get; init; }
#pragma warning restore SA1623 // Property summary documentation should match accessors

    /// <inheritdoc cref="StatusCode"/>
    public string Message { get; init; } = null!;

    /// <inheritdoc cref="StatusCode"/>
    public string ContentType { get; init; } = null!;

    /// <summary>
    /// Creates a new <see cref="ResponseExceptionInfo" /> instance from an <see cref="ApiException" />.
    /// </summary>
    /// <param name="exception">The exception to create the <see cref="ResponseExceptionInfo" /> from.</param>
    [NullChecks(true)]
    public static ResponseExceptionInfo FromApiException(ApiException exception)
    {
        string contentType = "text/plain";
        int statusCode = (int)exception.StatusCode;
        string message = exception.Message;
        return new(statusCode, message, contentType);
    }

    /// <summary>
    /// Creates a new <see cref="ResponseExceptionInfo" /> instance from an <see cref="Exception" />.
    /// </summary>
    /// <param name="exception">The exception to create the <see cref="ResponseExceptionInfo" /> from.</param>
    /// <param name="developmentMode">Whether to include detailed exception information for development purposes.</param>
    [NullChecks(true)]
    public static ResponseExceptionInfo FromException(Exception exception, bool developmentMode = false)
    {
        if (exception is ApiException ae)
        {
            return FromApiException(ae);
        }
        else if (developmentMode)
        {
            string contentType = "text/plain";
            int statusCode = 500;
            string message = $"{exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}";
            return new ResponseExceptionInfo(statusCode, message, contentType);
        }
        else
        {
            string contentType = "text/plain";
            int statusCode = 500;
            string message = string.Empty;
            return new ResponseExceptionInfo(statusCode, message, contentType);
        }
    }
}
