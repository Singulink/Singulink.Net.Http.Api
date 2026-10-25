using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.SignalR;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Hub filter that reports exceptions thrown by hub methods (including during enumeration of streamed results) and connection handlers to clients as API
/// errors. Exceptions are mapped using the registered <see cref="IApiExceptionHandler"/> (if any), so hub exceptions are logged and reported consistently
/// with regular and streaming API responses, and the resulting <see cref="ApiException"/> is sent to the client in a form that the API client reconstructs
/// as the same exception type.
/// </summary>
/// <remarks>
/// Register the filter via <see cref="ApiHubOptionsExtensions.AddApiExceptionFilter(HubOptions)"/>. Exceptions that are not mapped to an
/// <see cref="ApiException"/> (i.e. unexpected exceptions with no handler registered) propagate to SignalR's default handling. Streamed results are only
/// wrapped for hub methods that return <see cref="IAsyncEnumerable{T}"/> (directly or via a task); exceptions from channel-based streams are not translated.
/// In apps without dynamic code, such as Native AOT apps, exceptions thrown while streaming value type items are not translated either.
/// </remarks>
public sealed class ApiExceptionHubFilter : IHubFilter
{
    private const string WrapStreamJustification =
        "WrapStream has no annotated generic parameters. Reference type instantiations share code, and value types are only used when dynamic code is " +
        "supported.";

    private static readonly MethodInfo WrapStreamMethod =
        typeof(ApiExceptionHubFilter).GetMethod(nameof(WrapStream), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConditionalWeakTable<MethodInfo, StrongBox<Func<object, HubCallerContext, object>?>> WrapperCache = [];

    /// <inheritdoc/>
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        object? result;

        try
        {
            result = await next(invocationContext);
        }
        catch (Exception ex) when (!invocationContext.Context.ConnectionAborted.IsCancellationRequested)
        {
            var exception = await GetExceptionToThrowAsync(invocationContext.Context, ex);

            if (exception == ex)
                throw;

            ExceptionDispatchInfo.Throw(exception);
            throw;
        }

        // Streamed results are enumerated by SignalR after the method returns, so wrap them to translate exceptions thrown during enumeration as well.

        if (result is not null && GetStreamWrapper(invocationContext.HubMethod) is { } wrap)
            return wrap(result, invocationContext.Context);

        return result;
    }

    /// <inheritdoc/>
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Context.ConnectionAborted.IsCancellationRequested)
        {
            var exception = await GetExceptionToThrowAsync(context.Context, ex);

            if (exception == ex)
                throw;

            ExceptionDispatchInfo.Throw(exception);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        return next(context, exception);
    }

    /// <summary>
    /// Gets the exception to report for an exception thrown by a hub method, a streamed result or a connection handler: a <see cref="HubException"/> with
    /// the API error if the exception maps to an API exception, otherwise the exception itself. Without dynamic code, SignalR invokes hub methods with
    /// reflection, which wraps their exceptions in a <see cref="TargetInvocationException"/>, so those are unwrapped first.
    /// </summary>
    internal static async ValueTask<Exception> GetExceptionToThrowAsync(HubCallerContext callerContext, Exception exception)
    {
        if (exception is TargetInvocationException { InnerException: { } innerException })
            exception = innerException;

        if (exception is HubException || callerContext.GetHttpContext() is not { } httpContext)
            return exception;

        if (await ApiExceptionHandling.ResolveAsync(httpContext, exception) is not { } apiException)
            return exception;

        return new HubException(ResponseExceptionInfo.FromApiException(apiException).ToHubErrorString(), apiException);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = WrapStreamJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = WrapStreamJustification)]
    private static Func<object, HubCallerContext, object>? GetStreamWrapper(MethodInfo hubMethod)
    {
        if (WrapperCache.TryGetValue(hubMethod, out var cached))
            return cached.Value;

        Func<object, HubCallerContext, object>? wrapper = null;

        // Without dynamic code, a value type instantiation can only exist if it was compiled ahead of time, which it isn't.
        if (GetStreamItemType(hubMethod.ReturnType) is { } itemType && (!itemType.IsValueType || RuntimeFeature.IsDynamicCodeSupported))
            wrapper = WrapStreamMethod.MakeGenericMethod(itemType).CreateDelegate<Func<object, HubCallerContext, object>>();

        WrapperCache.AddOrUpdate(hubMethod, new StrongBox<Func<object, HubCallerContext, object>?>(wrapper));
        return wrapper;
    }

    /// <summary>
    /// Gets the item type for hub method return types of the form <c>IAsyncEnumerable&lt;T&gt;</c>, <c>Task&lt;IAsyncEnumerable&lt;T&gt;&gt;</c> or
    /// <c>ValueTask&lt;IAsyncEnumerable&lt;T&gt;&gt;</c>, otherwise <see langword="null"/>.
    /// </summary>
    private static Type? GetStreamItemType(Type returnType)
    {
        if (!returnType.IsGenericType)
            return null;

        var definition = returnType.GetGenericTypeDefinition();

        if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
        {
            returnType = returnType.GetGenericArguments()[0];

            if (!returnType.IsGenericType)
                return null;

            definition = returnType.GetGenericTypeDefinition();
        }

        return definition == typeof(IAsyncEnumerable<>) ? returnType.GetGenericArguments()[0] : null;
    }

    private static object WrapStream<T>(object source, HubCallerContext callerContext) => new TranslatingStream<T>((IAsyncEnumerable<T>)source, callerContext);

    private sealed class TranslatingStream<T>(IAsyncEnumerable<T> source, HubCallerContext callerContext) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new Enumerator(source.GetAsyncEnumerator(cancellationToken), callerContext);
        }

        private sealed class Enumerator(IAsyncEnumerator<T> inner, HubCallerContext callerContext) : IAsyncEnumerator<T>
        {
            public T Current => inner.Current;

            public async ValueTask<bool> MoveNextAsync()
            {
                try
                {
                    return await inner.MoveNextAsync();
                }
                catch (Exception ex) when (!callerContext.ConnectionAborted.IsCancellationRequested)
                {
                    var exception = await GetExceptionToThrowAsync(callerContext, ex);

                    if (exception == ex)
                        throw;

                    ExceptionDispatchInfo.Throw(exception);
                    throw;
                }
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
