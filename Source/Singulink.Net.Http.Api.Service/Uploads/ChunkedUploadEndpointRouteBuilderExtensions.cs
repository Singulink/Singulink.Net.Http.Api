using System.Diagnostics.CodeAnalysis;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for mapping chunked upload endpoints in an <see cref="IEndpointRouteBuilder"/>.
/// </summary>
public static class ChunkedUploadEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps an upload endpoint that receives a file in chunks and runs a handler with the complete file.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The route pattern of the endpoint. Its uploads are under it, at <c>{pattern}/uploads</c>.</param>
    /// <param name="handler">The handler, which runs once all chunks have arrived. It takes an <see cref="UploadedFile"/>, or an
    /// <see cref="UploadedFile{TCommand}"/> with the app's command, and binds its other parameters as any endpoint does. It returns its result, or an
    /// <see cref="IAsyncEnumerable{T}"/> of items, of a reference type.</param>
    /// <param name="configure">An optional action that configures the options of the endpoint.</param>
    /// <returns>A builder for adding conventions to all the endpoints of the upload, such as rate limits.</returns>
    /// <inheritdoc cref="MapChunkedUpload(IEndpointRouteBuilder, string, Delegate, Delegate, Action{ChunkedUploadEndpointOptions}?)" path="/remarks"/>
    /// <inheritdoc cref="MapChunkedUpload(IEndpointRouteBuilder, string, Delegate, Delegate, Action{ChunkedUploadEndpointOptions}?)" path="/exception"/>
    [RequiresUnreferencedCode(UploadEndpoint.RequiresReflectionMessage)]
    [RequiresDynamicCode(UploadEndpoint.RequiresReflectionMessage)]
    public static RouteGroupBuilder MapChunkedUpload(
        this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string pattern, Delegate handler, Action<ChunkedUploadEndpointOptions>? configure = null)
    {
        return MapCore(endpoints, pattern, null, handler, configure);
    }

    /// <summary>
    /// Maps an upload endpoint that receives a file in chunks, with a validator that runs when an upload is created and a handler that runs with the
    /// complete file.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The route pattern of the endpoint. Its uploads are under it, at <c>{pattern}/uploads</c>.</param>
    /// <param name="validator">The validator, which runs when an upload is created, before any chunk is sent. It takes an <see cref="UploadRequest"/>, or
    /// an <see cref="UploadRequest{TCommand}"/> with the app's command, and binds its other parameters as any endpoint does. It returns nothing, and
    /// rejects an upload by throwing an <see cref="ApiException"/>.</param>
    /// <param name="handler">The handler, which runs once all chunks have arrived. It takes an <see cref="UploadedFile"/>, or an
    /// <see cref="UploadedFile{TCommand}"/> with the app's command, and binds its other parameters as any endpoint does. It returns its result, or an
    /// <see cref="IAsyncEnumerable{T}"/> of items, of a reference type.</param>
    /// <param name="configure">An optional action that configures the options of the endpoint.</param>
    /// <returns>A builder for adding conventions to all the endpoints of the upload, such as rate limits.</returns>
    /// <remarks>
    /// <para>
    /// The endpoints are <c>POST {pattern}/uploads</c>, which creates an upload, <c>PUT</c>, <c>GET</c> and <c>DELETE</c> on
    /// <c>{pattern}/uploads/{id}</c>, which send a chunk, get the upload's status and abort it, and <c>POST {pattern}/uploads/{id}/complete</c>, which runs
    /// the handler and streams its result or items, with ping records while it runs. Every request needs a session of the user who created the upload,
    /// and the endpoint and route values that it was created with. The validator and the handler bind route values and the query string as any endpoint
    /// does, and take no request body.
    /// </para>
    /// <para>
    /// The handler runs at most once for an upload, and keeps running when the client disconnects: a repeated completion request attaches to the run, or
    /// gets its outcome once it has ended. Its cancellation token is cancelled when the upload is aborted, when it has run for
    /// <see cref="ChunkedUploadOptions.IdleExpiry"/>, and when the application stops, which ends the upload with
    /// <see cref="UploadErrorCodes.Interrupted"/>. A handler that fails with a <c>503</c>, a <see cref="ServiceUnavailableApiException"/> or an exception
    /// that the app's <see cref="IApiExceptionHandler"/> maps to one, leaves the upload with all its chunks and its file, so that the client's next completion
    /// request runs it again.
    /// </para>
    /// <para>
    /// The validator and the handler are built with <see cref="RequestDelegateFactory"/>, as for <c>MapPost(string, Delegate)</c>, which is not supported in
    /// trimmed and Native AOT apps.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The services of chunked uploads are not registered (see
    /// <see cref="ServiceCollectionExtensions.AddChunkedUploads{TSessionToken}(IServiceCollection, Action{ChunkedUploadOptions}?)"/>), or the validator or
    /// the handler has a signature that is not supported.</exception>
    [RequiresUnreferencedCode(UploadEndpoint.RequiresReflectionMessage)]
    [RequiresDynamicCode(UploadEndpoint.RequiresReflectionMessage)]
    public static RouteGroupBuilder MapChunkedUpload(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        Delegate validator,
        Delegate handler,
        Action<ChunkedUploadEndpointOptions>? configure = null)
    {
        return MapCore(endpoints, pattern, validator, handler, configure);
    }

    [RequiresUnreferencedCode(UploadEndpoint.RequiresReflectionMessage)]
    [RequiresDynamicCode(UploadEndpoint.RequiresReflectionMessage)]
    private static RouteGroupBuilder MapCore(
        IEndpointRouteBuilder endpoints, string pattern, Delegate? validator, Delegate handler, Action<ChunkedUploadEndpointOptions>? configure)
    {
        var uploads = endpoints.ServiceProvider.GetService<ChunkedUploads>() ?? throw new InvalidOperationException(
            "Chunked uploads require their services. Register them with AddChunkedUploads.");

        var options = new ChunkedUploadEndpointOptions();
        configure?.Invoke(options);

        var endpoint = UploadEndpoint.Create(endpoints.ServiceProvider, validator, handler, options);

        const string Upload = $"/{{{ChunkedUploads.IdRouteParameter}}}";
        var group = endpoints.MapGroup(pattern).MapGroup("/" + ChunkedUploadProtocol.UploadsSegment);

        group.MapPost(string.Empty, (RequestDelegate)(context => uploads.CreateAsync(context, endpoint)));
        group.MapGet(Upload, (RequestDelegate)(context => uploads.GetStatusAsync(context, endpoint)));
        group.MapDelete(Upload, (RequestDelegate)(context => uploads.AbortAsync(context, endpoint)));

        // A chunk takes as long as the client's connection needs, and a completion as long as the handler runs, so request timeouts don't apply to them.
        group.MapPut(Upload, (RequestDelegate)(context => uploads.WriteChunkAsync(context, endpoint))).DisableRequestTimeout();

        group.MapPost($"{Upload}/{ChunkedUploadProtocol.CompleteSegment}", (RequestDelegate)(context => uploads.CompleteAsync(context, endpoint)))
            .DisableRequestTimeout();

        return group;
    }
}
