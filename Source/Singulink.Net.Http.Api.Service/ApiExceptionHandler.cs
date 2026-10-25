namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Base <see cref="IApiExceptionHandler"/> implementation that reports <see cref="ApiException"/> instances as <see cref="MapApiExceptionAsync"/> returns
/// them (as-is by default), applies application-specific mappings via <see cref="MapAsync"/>, reports requests that ASP.NET Core could not read
/// (<see cref="BadHttpRequestException"/>) with their status code and a general message (ASP.NET Core's message in development environments), and handles
/// unexpected exceptions by logging them with a reference ID and reporting a <see cref="ServerErrorApiException"/> with the reference ID (see
/// <see cref="ApiException.ReferenceId"/>). In development environments unexpected exceptions are propagated instead so that the developer exception page
/// can display them.
/// </summary>
/// <remarks>
/// A class derived from it maps the application's own exceptions in <see cref="MapAsync"/>, the <see cref="ApiException"/> instances in
/// <see cref="MapApiExceptionAsync"/>, and unexpected exceptions in <see cref="HandleUnexpectedAsync"/>.
/// <see cref="ServiceCollectionExtensions.AddApiExceptionHandler(IServiceCollection)"/> registers the default behavior.
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
            return await MapApiExceptionAsync(httpContext, apiException);

        if (await MapAsync(httpContext, exception) is { } mappedException)
            return mappedException;

        if (exception is BadHttpRequestException badRequestException)
            return BadRequestMapping.ToApiException(httpContext, badRequestException, _environment.IsDevelopment());

        return await HandleUnexpectedAsync(httpContext, exception);
    }

    /// <summary>
    /// Maps an exception that is an <see cref="ApiException"/>, such as an error of the toolkit with an error code (see <see cref="ApiException.ErrorCode"/>),
    /// to the <see cref="ApiException"/> that is reported, such as one with a message of the application's own. The default implementation returns the
    /// exception as it is. <see cref="MapAsync"/> is not called for such an exception.
    /// </summary>
    protected virtual ValueTask<ApiException> MapApiExceptionAsync(HttpContext httpContext, ApiException exception) => new(exception);

    /// <summary>
    /// Maps application-specific exceptions to the <see cref="ApiException"/> that should be reported to the client (e.g. validation library exceptions to a
    /// <see cref="ValidationApiException"/>). Returns <see langword="null"/> (the default) if the exception is not recognized, in which case a
    /// <see cref="BadHttpRequestException"/> is reported with its status code and other exceptions are treated as unexpected.
    /// </summary>
    protected virtual ValueTask<ApiException?> MapAsync(HttpContext httpContext, Exception exception) => default;

    /// <summary>
    /// Handles an exception that was not recognized by <see cref="MapAsync"/>. The default implementation returns <see langword="null"/> if
    /// <see cref="PropagateUnexpectedExceptions"/> is <see langword="true"/>, otherwise it generates a reference ID, logs the exception via
    /// <see cref="LogUnexpected"/> and returns a <see cref="ServerErrorApiException"/> with the reference ID (see <see cref="ApiException.ReferenceId"/>)
    /// and the message produced by <see cref="CreateServerErrorMessage"/>.
    /// </summary>
    protected virtual ValueTask<ApiException?> HandleUnexpectedAsync(HttpContext httpContext, Exception exception)
    {
        if (PropagateUnexpectedExceptions)
            return default;

        var referenceId = Guid.CreateVersion7();
        LogUnexpected(httpContext, exception, referenceId);

        return new ValueTask<ApiException?>(new ServerErrorApiException(CreateServerErrorMessage(referenceId), exception) { ReferenceId = referenceId });
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
        return $"An unexpected error has occurred and has been logged. Reference ID: {referenceId}.";
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
