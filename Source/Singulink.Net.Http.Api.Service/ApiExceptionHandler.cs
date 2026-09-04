namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Base <see cref="IApiExceptionHandler"/> implementation that reports <see cref="ApiException"/> instances as-is, applies application-specific mappings via
/// <see cref="MapAsync"/>, and handles unexpected exceptions by logging them with a reference ID and reporting a <see cref="ServerErrorApiException"/> that
/// includes the reference ID. In development environments unexpected exceptions are propagated instead so that the developer exception page can display them.
/// </summary>
/// <remarks>
/// Derive from this class to add application-specific exception mappings or customize the unexpected exception handling, or register the default behavior
/// directly via <see cref="ServiceCollectionExtensions.AddApiExceptionHandler(IServiceCollection)"/>.
/// </remarks>
public abstract class ApiExceptionHandler : IApiExceptionHandler
{
    private readonly ILogger _logger;
    private readonly IHostEnvironment _environment;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiExceptionHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger used to log unexpected exceptions.</param>
    /// <param name="environment">The host environment.</param>
    protected ApiExceptionHandler(ILogger logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    /// <summary>
    /// Gets a value indicating whether unexpected exceptions should propagate unhandled instead of being logged and reported as a server error. Defaults to
    /// <see langword="true"/> in development environments so that the developer exception page can display them (and streaming responses report full
    /// exception details).
    /// </summary>
    protected virtual bool PropagateUnexpectedExceptions => _environment.IsDevelopment();

    /// <inheritdoc/>
    public async ValueTask<ApiException?> HandleAsync(HttpContext httpContext, Exception exception)
    {
        if (exception is ApiException apiException)
            return apiException;

        if (await MapAsync(httpContext, exception) is { } mappedException)
            return mappedException;

        return await HandleUnexpectedAsync(httpContext, exception);
    }

    /// <summary>
    /// Maps application-specific exceptions to the <see cref="ApiException"/> that should be reported to the client (e.g. validation library exceptions to a
    /// <see cref="ValidationApiException"/>). Returns <see langword="null"/> (the default) if the exception is not recognized, in which case it is treated as
    /// unexpected.
    /// </summary>
    protected virtual ValueTask<ApiException?> MapAsync(HttpContext httpContext, Exception exception) => default;

    /// <summary>
    /// Handles an exception that was not recognized by <see cref="MapAsync"/>. The default implementation returns <see langword="null"/> if
    /// <see cref="PropagateUnexpectedExceptions"/> is <see langword="true"/>, otherwise it generates a reference ID, logs the exception via
    /// <see cref="LogUnexpected"/> and returns a <see cref="ServerErrorApiException"/> with the message produced by <see cref="CreateServerErrorMessage"/>.
    /// </summary>
    protected virtual ValueTask<ApiException?> HandleUnexpectedAsync(HttpContext httpContext, Exception exception)
    {
        if (PropagateUnexpectedExceptions)
            return default;

        var referenceId = Guid.CreateVersion7();
        LogUnexpected(httpContext, exception, referenceId);

        return new ValueTask<ApiException?>(new ServerErrorApiException(CreateServerErrorMessage(referenceId), exception));
    }

    /// <summary>
    /// Logs an unexpected exception. The default implementation logs an error with the request method, path and reference ID as structured properties.
    /// </summary>
    protected virtual void LogUnexpected(HttpContext httpContext, Exception exception, Guid referenceId)
    {
        _logger.LogError(
            exception,
            "Unhandled exception while processing {Method} {Path} (reference ID: {ReferenceId}).",
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            referenceId);
    }

    /// <summary>
    /// Creates the message reported to the client for an unexpected exception that was logged with the specified reference ID.
    /// </summary>
    protected virtual string CreateServerErrorMessage(Guid referenceId)
    {
        return $"An unexpected error has occurred and has been logged. Reference ID: {referenceId}";
    }
}

/// <summary>
/// The default <see cref="ApiExceptionHandler"/> with no application-specific mappings.
/// </summary>
internal sealed class DefaultApiExceptionHandler : ApiExceptionHandler
{
    public DefaultApiExceptionHandler(ILogger<DefaultApiExceptionHandler> logger, IHostEnvironment environment) : base(logger, environment)
    {
    }
}
