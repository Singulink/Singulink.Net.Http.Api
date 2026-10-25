using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// An upload endpoint mapped with <c>MapChunkedUpload</c>: its validator and handler, built with <see cref="RequestDelegateFactory"/> as
/// <c>MapPost(Delegate)</c> builds a handler, and what the service needs to know about them.
/// </summary>
internal sealed class UploadEndpoint
{
    internal const string RequiresReflectionMessage =
        "Chunked uploads build the validator and the handler with RequestDelegateFactory, as MapPost(Delegate) does, which uses reflection on the " +
        "delegates and their parameters. These types may be trimmed if not directly referenced.";

    private readonly ConcurrentDictionary<Endpoint, string> _keys = [];

    private UploadEndpoint(RequestDelegate? validator, RequestDelegate handler, Type itemType, bool isStreaming)
    {
        Validator = validator;
        Handler = handler;
        ItemType = itemType;
        IsStreaming = isStreaming;
    }

    /// <summary>
    /// Gets the validator, which runs at the creation of an upload, or <see langword="null"/> if there is none.
    /// </summary>
    public RequestDelegate? Validator { get; }

    /// <summary>
    /// Gets the handler, which runs once all chunks have arrived.
    /// </summary>
    public RequestDelegate Handler { get; }

    /// <summary>
    /// Gets the declared type of the handler's items, or of its result if it returns a single one.
    /// </summary>
    public Type ItemType { get; }

    /// <summary>
    /// Gets a value indicating whether the handler returns an <see cref="IAsyncEnumerable{T}"/> of items rather than a single result.
    /// </summary>
    public bool IsStreaming { get; }

    /// <summary>
    /// Gets the type of the app's command, if the validator or the handler takes one.
    /// </summary>
    public Type? CommandType { get; private init; }

    /// <summary>
    /// Gets a value indicating whether the command is required, because a parameter that takes it does not allow <see langword="null"/>.
    /// </summary>
    public bool IsCommandRequired { get; private init; }

    /// <summary>
    /// Gets the ping interval that a <see cref="KeepAlivePingAttribute"/> on the handler sets, if it has one.
    /// </summary>
    public TimeSpan? PingInterval { get; private init; }

    /// <summary>
    /// Gets the largest upload of the endpoint, if it has a limit of its own.
    /// </summary>
    public long? MaxSize { get; private init; }

    [RequiresUnreferencedCode(RequiresReflectionMessage)]
    [RequiresDynamicCode(RequiresReflectionMessage)]
    public static UploadEndpoint Create(IServiceProvider services, Delegate? validator, Delegate handler, ChunkedUploadEndpointOptions options)
    {
        var handlerMethod = handler.Method;
        var (itemType, isStreaming) = GetItemType(handlerMethod);

        if (validator is not null)
        {
            var returnType = validator.Method.ReturnType;

            if (returnType != typeof(void) && returnType != typeof(Task) && returnType != typeof(ValueTask))
            {
                throw new InvalidOperationException(
                    $"The validator of a chunked upload returns '{returnType}', but must return void, Task or ValueTask. It reports a rejected upload by " +
                    "throwing an ApiException.");
            }
        }

        var handlerParameters = handlerMethod.GetParameters();
        var validatorParameters = validator?.Method.GetParameters() ?? [];

        if (handlerParameters.FirstOrDefault(p => typeof(UploadRequest).IsAssignableFrom(p.ParameterType)) is { } handlerRequest)
        {
            throw new InvalidOperationException(
                $"The handler of a chunked upload takes an {nameof(UploadRequest)} parameter ('{handlerRequest.Name}'), which only validators take. " +
                $"Take an {nameof(UploadedFile)} instead.");
        }

        if (validatorParameters.FirstOrDefault(p => typeof(UploadedFile).IsAssignableFrom(p.ParameterType)) is { } validatorFile)
        {
            throw new InvalidOperationException(
                $"The validator of a chunked upload takes an {nameof(UploadedFile)} parameter ('{validatorFile.Name}'), which only handlers take, since " +
                $"validators run before the file is sent. Take an {nameof(UploadRequest)} instead.");
        }

        var commandParameters = handlerParameters.Concat(validatorParameters).Where(p => UploadCommands.GetCommandType(p.ParameterType) is not null).ToList();
        var commandTypes = commandParameters.Select(p => UploadCommands.GetCommandType(p.ParameterType)!).Distinct().ToList();

        if (commandTypes.Count > 1)
        {
            throw new InvalidOperationException(
                $"The validator and the handler of a chunked upload take commands of different types ({string.Join(", ", commandTypes)}). Take the same " +
                "command type in both.");
        }

        return new UploadEndpoint(validator is null ? null : CreateDelegate(validator, services), CreateDelegate(handler, services), itemType, isStreaming) {
            CommandType = commandTypes.FirstOrDefault(),
            IsCommandRequired = commandParameters.Any(UploadCommands.IsRequired),
            PingInterval = handlerMethod.GetCustomAttribute<KeepAlivePingAttribute>()?.Interval,
            MaxSize = options.MaxSize,
        };
    }

    /// <summary>
    /// Gets the key of the endpoint that the request is for, which identifies the upload endpoint across servers and restarts: the route pattern of the
    /// endpoint without the segments of the toolkit's own endpoints under it, such as <c>/folders/{folderId}/files</c>.
    /// </summary>
    /// <param name="context">The request's context.</param>
    /// <param name="ownSegmentCount">The number of segments of the toolkit's own endpoint at the end of the route pattern, such as 1 for
    /// <c>uploads</c>.</param>
    public string GetKey(HttpContext context, int ownSegmentCount)
    {
        var endpoint = context.GetEndpoint() as RouteEndpoint ?? throw new InvalidOperationException("The request has no route endpoint.");
        return _keys.GetOrAdd(endpoint, static (e, count) => CreateKey(((RouteEndpoint)e).RoutePattern, count), ownSegmentCount);
    }

    private static string CreateKey(RoutePattern pattern, int ownSegmentCount)
    {
        var key = new StringBuilder();

        foreach (var segment in pattern.PathSegments.Take(pattern.PathSegments.Count - ownSegmentCount))
        {
            key.Append('/');

            foreach (var part in segment.Parts)
            {
                if (part is RoutePatternLiteralPart literal)
                    key.Append(literal.Content.ToLowerInvariant());
                else if (part is RoutePatternParameterPart parameter)
                    key.Append('{').Append(parameter.Name).Append('}');
                else if (part is RoutePatternSeparatorPart separator)
                    key.Append(separator.Content);
            }
        }

        return key.Length is 0 ? "/" : key.ToString();
    }

    /// <summary>
    /// Gets the item type of a handler that returns <c>IAsyncEnumerable&lt;T&gt;</c>, or the result type of one that returns a single result, either of
    /// them also wrapped in a <see cref="Task{TResult}"/> or <see cref="ValueTask{TResult}"/>.
    /// </summary>
    private static (Type ItemType, bool IsStreaming) GetItemType(MethodInfo method)
    {
        var returnType = method.ReturnType;

        if (returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            throw new InvalidOperationException(
                "The handler of a chunked upload returns no result. Return the result, such as Task<T>, or IAsyncEnumerable<T> for a handler that reports " +
                "its progress as items.");
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() is { } definition && (definition == typeof(Task<>) ||
            definition == typeof(ValueTask<>)))
        {
            returnType = returnType.GetGenericArguments()[0];
        }

        bool isStreaming = returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);
        var itemType = isStreaming ? returnType.GetGenericArguments()[0] : returnType;

        if (itemType.IsValueType)
        {
            throw new InvalidOperationException(
                $"The handler of a chunked upload returns a value type ('{itemType.Name}'), which is not supported for its result or items. Wrap the value " +
                "in a record or class.");
        }

        if (typeof(IResult).IsAssignableFrom(itemType))
        {
            throw new InvalidOperationException(
                $"The handler of a chunked upload returns an {nameof(IResult)} ('{itemType.Name}'), which is not supported: its result or items are written " +
                "to the completion stream. Return the result itself, and report errors by throwing an ApiException.");
        }

        return (itemType, isStreaming);
    }

    /// <summary>
    /// Builds the request delegate of a validator or handler, which binds its parameters from the request and calls it. A filter keeps its result in the
    /// request's <see cref="HandlerInvocation"/> instead of writing it to the response.
    /// </summary>
    [RequiresUnreferencedCode(RequiresReflectionMessage)]
    [RequiresDynamicCode(RequiresReflectionMessage)]
    private static RequestDelegate CreateDelegate(Delegate handler, IServiceProvider services)
    {
        var builder = new RouteEndpointBuilder(null, RoutePatternFactory.Parse("/"), 0) { ApplicationServices = services };

        builder.FilterFactories.Add((_, next) => async context => {
            var invocation = HandlerInvocation.Get(context.HttpContext);
            invocation.IsInvoked = true;
            invocation.Result = await next(context);

            return Results.Empty;
        });

        var options = new RequestDelegateFactoryOptions {
            ServiceProvider = services,
            EndpointBuilder = builder,

            // The request bodies of the toolkit's endpoints are the toolkit's, so parameters without a binding source are services rather than the body.
            DisableInferBodyFromParameters = true,

            // A request that the parameters cannot be bound from throws, so that it is reported as an API error whatever the app's settings.
            ThrowOnBadRequest = true,
        };

        return RequestDelegateFactory.Create(handler, options).RequestDelegate;
    }
}

/// <summary>
/// The call of a validator or handler in a request: whether it was called, which means that its parameters were bound, and its result.
/// </summary>
internal sealed class HandlerInvocation
{
    private static readonly object Key = new();

    public bool IsInvoked { get; set; }

    public object? Result { get; set; }

    public static HandlerInvocation Get(HttpContext context)
    {
        return context.Items.TryGetValue(Key, out object? value) && value is HandlerInvocation invocation ? invocation :
            throw new InvalidOperationException("The validator or handler of a chunked upload was called outside of its request.");
    }

    public static HandlerInvocation Start(HttpContext context)
    {
        var invocation = new HandlerInvocation();
        context.Items[Key] = invocation;

        return invocation;
    }
}
