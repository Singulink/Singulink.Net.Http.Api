using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Options for configuring the <see cref="ResponseSideInfoEnumerationEndpointFilter" /> to handle response side info (such as API exceptions) for enumerable
/// results.
/// </summary>
public sealed class ResponseSideInfoEnumerationEndpointFilterOptions
{
    /// <summary>
    /// Adds a new enumerable type to the list of types to handle for enumeration.
    /// </summary>
    public ResponseSideInfoEnumerationEndpointFilterOptions Add<T>()
        where T : class, ISupportsResponseSideInfo<T>
    {
        _enumerableTypes.Add(new Helper<T>());
        return this;
    }

    /// <summary>
    /// Adds a callback that will be invoked whenever an unhandled exception is thrown during enumeration of an enumerable result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exceptions are always converted into the enumerable result, but by default they are only logged to trace. Adding a callback removes the default logging
    /// behaviour and allows you to customize behaviour.
    /// </para>
    /// <para>
    /// Note: this is called before the exception is converted and reported from the endpoint.
    /// </para>
    /// </remarks>
    public ResponseSideInfoEnumerationEndpointFilterOptions AddExceptionObserver(Action<Exception> callback)
    {
        _unhandledExceptionCallback += callback;
        return this;
    }

    internal Action<Exception>? _unhandledExceptionCallback;

    internal readonly List<HelperBase> _enumerableTypes = [];

    internal abstract class HelperBase
    {
        public abstract bool TryWrap(
            IAsyncEnumerable<ISupportsResponseSideInfo?> enumerable,
            bool isDevelopment,
            Action<Exception>? unhandledExceptionCallback,
            TimeSpan? pingInterval,
            [NotNullWhen(true)] out IAsyncEnumerable<ISupportsResponseSideInfo?>? wrapped);

        public abstract bool TryWrap(
            IEnumerable<ISupportsResponseSideInfo?> enumerable,
            bool isDevelopment,
            Action<Exception>? unhandledExceptionCallback,
            [NotNullWhen(true)] out IEnumerable<ISupportsResponseSideInfo?>? wrapped);
    }

    private static void DefaultUnhandledExceptionCallback(Exception ex)
    {
        if (ex is ApiException apiEx)
        {
            if (Trace.Listeners.Count > 0)
                Trace.TraceWarning($"[Singulink.Net.Http.Api] Expected API exception handled ({apiEx.StatusCode}): {apiEx}");
        }
        else
        {
            if (Trace.Listeners.Count > 0)
                Trace.TraceWarning($"[Singulink.Net.Http.Api] Unexpected exception suppressed: {ex}");
        }
    }

    private sealed class Helper<T> : HelperBase
        where T : class, ISupportsResponseSideInfo<T>
    {
        public override bool TryWrap(
            IAsyncEnumerable<ISupportsResponseSideInfo?> enumerable,
            bool isDevelopment,
            Action<Exception>? unhandledExceptionCallback,
            TimeSpan? pingInterval,
            [NotNullWhen(true)] out IAsyncEnumerable<ISupportsResponseSideInfo?>? wrapped)
        {
            if (enumerable is IAsyncEnumerable<T?> typedEnumerable)
            {
                wrapped = new AsyncEnumerableWrapper(typedEnumerable, isDevelopment, unhandledExceptionCallback ?? DefaultUnhandledExceptionCallback, pingInterval);
                return true;
            }

            wrapped = null;
            return false;
        }

        public override bool TryWrap(
            IEnumerable<ISupportsResponseSideInfo?> enumerable,
            bool isDevelopment,
            Action<Exception>? unhandledExceptionCallback,
            [NotNullWhen(true)] out IEnumerable<ISupportsResponseSideInfo?>? wrapped)
        {
            if (enumerable is IEnumerable<T?> typedEnumerable)
            {
                wrapped = new EnumerableWrapper(typedEnumerable, isDevelopment, unhandledExceptionCallback ?? DefaultUnhandledExceptionCallback);
                return true;
            }

            wrapped = null;
            return false;
        }

        private sealed class AsyncEnumerableWrapper : IAsyncEnumerable<T?>
        {
            private readonly IAsyncEnumerable<T?> _enumerable;
            private readonly bool _isDevelopment;
            private readonly Action<Exception> _unhandledExceptionCallback;
            private readonly TimeSpan? _pingInterval;

            public AsyncEnumerableWrapper(IAsyncEnumerable<T?> enumerable, bool isDevelopment, Action<Exception> unhandledExceptionCallback, TimeSpan? pingInterval)
            {
                _enumerable = enumerable;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
                _pingInterval = pingInterval;
            }

            public IAsyncEnumerator<T?> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                // Link the token passed to the underlying enumerator so that a pending MoveNextAsync can be cancelled promptly when the wrapper is disposed.
                var disposeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                return new AsyncEnumeratorWrapper(_enumerable.GetAsyncEnumerator(disposeCts.Token), _isDevelopment, _unhandledExceptionCallback, _pingInterval, cancellationToken, disposeCts);
            }
        }

        private sealed class AsyncEnumeratorWrapper : IAsyncEnumerator<T?>
        {
            private IAsyncEnumerator<T?>? _enumerator;
            private readonly bool _isDevelopment;
            private readonly Action<Exception> _unhandledExceptionCallback;
            private readonly TimeSpan? _pingInterval;
            private readonly CancellationToken _cancellationToken;
            private readonly CancellationTokenSource _disposeCts;
            private Task<bool>? _pendingMoveNext;
            private T? _current;

            public AsyncEnumeratorWrapper(IAsyncEnumerator<T?> enumerator, bool isDevelopment, Action<Exception> unhandledExceptionCallback, TimeSpan? pingInterval, CancellationToken cancellationToken, CancellationTokenSource disposeCts)
            {
                _enumerator = enumerator;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
                _pingInterval = pingInterval;
                _cancellationToken = cancellationToken;
                _disposeCts = disposeCts;
            }

            public T? Current => _current;

            public async ValueTask DisposeAsync()
            {
                if (_enumerator is { } enumerator)
                {
                    _enumerator = null;
                    await ObservePendingMoveNextAsync();
                    await enumerator.DisposeAsync();
                }

                _disposeCts.Dispose();
                _current = null;
            }

            /// <summary>
            /// Observes any pending MoveNextAsync so it can complete before the underlying enumerator is disposed, cancelling it via the linked token if it
            /// hasn't already completed so that disposal can occur promptly.
            /// </summary>
            private async ValueTask ObservePendingMoveNextAsync()
            {
                if (_pendingMoveNext is { } pending)
                {
                    _pendingMoveNext = null;

                    // If it hasn't already completed, cancel the linked token so it can complete promptly, then suppress the resulting cancellation.
                    if (!pending.IsCompleted)
                        _disposeCts.Cancel();

                    try
                    {
                        await pending;
                    }
                    catch { }
                }
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                if (_enumerator == null)
                {
                    _current = null;
                    return false;
                }

                try
                {
                    ValueTask<bool> moveNext;

                    if (_pingInterval is not TimeSpan pingInterval)
                    {
                        moveNext = _enumerator.MoveNextAsync();
                        goto AwaitMoveNext;
                    }

                    Task<bool> moveNextTask;

                    if (_pendingMoveNext is { } pending)
                    {
                        _pendingMoveNext = null;
                        moveNextTask = pending;
                    }
                    else
                    {
                        moveNext = _enumerator.MoveNextAsync();

                        // Optimistically check for synchronous completion to avoid task/timer overhead.
                        if (moveNext.IsCompleted)
                            goto AwaitMoveNext;

                        moveNextTask = moveNext.AsTask();
                    }

                    // Create a delay task and observe any exception from the delay task so it doesn't throw on a background thread due to not being observed.
                    var delayTask = Task.Delay(pingInterval, _cancellationToken);
                    _ = delayTask.ContinueWith(
                        static t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    if (await Task.WhenAny(moveNextTask, delayTask) != moveNextTask)
                    {
                        // Store the still-pending move next so it is observed if the cancellation check throws or enumeration continues.
                        _pendingMoveNext = moveNextTask;

                        // Check the user's cancellation token in case we got here from that
                        _cancellationToken.ThrowIfCancellationRequested();

                        // Timed out waiting for the next item: emit a ping (an item with null side info, ignored by the client) and keep waiting next time.
                        _current = T.CreateResponseSideInfoValue(null);
                        _ = (IStoresResponseSideInfo)_current;
                        return true;
                    }

                    moveNext = new ValueTask<bool>(moveNextTask);

                    AwaitMoveNext:
                    bool hasNext = await moveNext;
                    _current = hasNext ? _enumerator.Current : null;
                    return hasNext;
                }
                catch (Exception ex)
                {
                    _unhandledExceptionCallback(ex);
                    _current = T.CreateResponseSideInfoValue(ResponseExceptionInfo.FromException(ex, _isDevelopment).ToEnumerationString());
                    _ = (IStoresResponseSideInfo)_current;

                    // Dispose the underlying enumerator since enumeration is terminated by the exception item.
                    if (_enumerator is { } enumerator)
                    {
                        _enumerator = null;

                        try
                        {
                            await ObservePendingMoveNextAsync();
                            await enumerator.DisposeAsync();
                        }
                        catch { }
                    }

                    return true;
                }
            }
        }

        private sealed class EnumerableWrapper : IEnumerable<T?>
        {
            private readonly IEnumerable<T?> _enumerable;
            private readonly bool _isDevelopment;
            private readonly Action<Exception> _unhandledExceptionCallback;

            public EnumerableWrapper(IEnumerable<T?> enumerable, bool isDevelopment, Action<Exception> unhandledExceptionCallback)
            {
                _enumerable = enumerable;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
            }

            public IEnumerator<T?> GetEnumerator()
            {
                return new EnumeratorWrapper(_enumerable.GetEnumerator(), _isDevelopment, _unhandledExceptionCallback);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        private sealed class EnumeratorWrapper : IEnumerator<T?>
        {
            private IEnumerator<T?>? _enumerator;
            private readonly bool _isDevelopment;
            private readonly Action<Exception> _unhandledExceptionCallback;
            private T? _current;

            public EnumeratorWrapper(IEnumerator<T?> enumerator, bool isDevelopment, Action<Exception> unhandledExceptionCallback)
            {
                _enumerator = enumerator;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
            }

            public T? Current => _current;

            object? IEnumerator.Current => _current;

            public void Dispose()
            {
                if (_enumerator != null)
                {
                    _enumerator.Dispose();
                    _enumerator = null;
                }

                _current = null;
            }

            public bool MoveNext()
            {
                if (_enumerator == null)
                {
                    _current = null;
                    return false;
                }

                try
                {
                    bool hasNext = _enumerator.MoveNext();
                    _current = hasNext ? _enumerator.Current : null;
                    return hasNext;
                }
                catch (Exception ex)
                {
                    _unhandledExceptionCallback(ex);
                    _current = T.CreateResponseSideInfoValue(ResponseExceptionInfo.FromException(ex, _isDevelopment).ToEnumerationString());
                    _ = (IStoresResponseSideInfo)_current;

                    // Dispose the underlying enumerator since enumeration is terminated by the exception item.
                    if (_enumerator is { } enumerator)
                    {
                        _enumerator = null;

                        try
                        {
                            enumerator.Dispose();
                        }
                        catch { }
                    }

                    return true;
                }
            }

            public void Reset()
            {
                throw new NotSupportedException("Reset is not supported.");
            }
        }
    }
}
