using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Hub filter that reports exceptions thrown by hub methods (including during enumeration of streamed results) and connection handlers to clients as API
/// errors. Exceptions are mapped using the registered <see cref="IApiExceptionHandler"/> (if any), so hub exceptions are logged and reported consistently
/// with regular and streaming API responses, and the resulting <see cref="ApiException"/> is sent to the client in a form that the API client reconstructs
/// as the same exception type.
/// </summary>
/// <remarks>
/// Register the filter via <see cref="HubOptionsExtensions.AddApiExceptionFilter(HubOptions)"/>. Exceptions that are not mapped to an
/// <see cref="ApiException"/> (i.e. unexpected exceptions with no handler registered) propagate to SignalR's default handling. Streamed results are only
/// wrapped for hub methods that return <see cref="IAsyncEnumerable{T}"/> (directly or via a task); exceptions from channel-based streams are not translated.
/// </remarks>
public sealed class ApiExceptionHubFilter : IHubFilter
{
    private const string DynamicCodeJustification =
        "SignalR hubs require dynamic code and unreferenced code (AddSignalR carries the same requirements), and the filter registration method is annotated " +
        "accordingly.";

    private static readonly MethodInfo WrapStreamMethod = typeof(ApiExceptionHubFilter).GetMethod(nameof(WrapStream), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConditionalWeakTable<MethodInfo, StrongBox<Func<object, HubCallerContext, object>?>> WrapperCache = [];

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = DynamicCodeJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = DynamicCodeJustification)]
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        object? result;

        try
        {
            result = await next(invocationContext);
        }
        catch (Exception ex) when (ex is not HubException && !invocationContext.Context.ConnectionAborted.IsCancellationRequested)
        {
            throw await TranslateAsync(invocationContext.Context, ex) ?? ex;
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
        catch (Exception ex) when (ex is not HubException && !context.Context.ConnectionAborted.IsCancellationRequested)
        {
            throw await TranslateAsync(context.Context, ex) ?? ex;
        }
    }

    /// <inheritdoc/>
    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        return next(context, exception);
    }

    private static async ValueTask<HubException?> TranslateAsync(HubCallerContext callerContext, Exception exception)
    {
        if (callerContext.GetHttpContext() is not { } httpContext)
            return null;

        if (await ApiExceptionHandling.ResolveAsync(httpContext, exception) is not { } apiException)
            return null;

        return new HubException(ResponseExceptionInfo.FromApiException(apiException).ToHubErrorString(), apiException);
    }

    [RequiresDynamicCode(DynamicCodeJustification)]
    [RequiresUnreferencedCode(DynamicCodeJustification)]
    private static Func<object, HubCallerContext, object>? GetStreamWrapper(MethodInfo hubMethod)
    {
        if (WrapperCache.TryGetValue(hubMethod, out var cached))
            return cached.Value;

        Func<object, HubCallerContext, object>? wrapper = null;

        if (GetStreamItemType(hubMethod.ReturnType) is { } itemType)
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
                catch (Exception ex) when (ex is not HubException && !callerContext.ConnectionAborted.IsCancellationRequested)
                {
                    throw await TranslateAsync(callerContext, ex) ?? ex;
                }
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}

/// <summary>
/// Extension methods for configuring <see cref="HubOptions"/>.
/// </summary>
public static class HubOptionsExtensions
{
    /// <summary>
    /// Adds the <see cref="ApiExceptionHubFilter"/> so that hub exceptions are reported to clients as API errors.
    /// </summary>
    [RequiresDynamicCode("SignalR hubs require dynamic code.")]
    [RequiresUnreferencedCode("SignalR hubs require unreferenced code.")]
    public static HubOptions AddApiExceptionFilter(this HubOptions options)
    {
        options.AddFilter<ApiExceptionHubFilter>();
        return options;
    }
}
