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
            TimeSpan? refreshInterval,
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
            TimeSpan? refreshInterval,
            [NotNullWhen(true)] out IAsyncEnumerable<ISupportsResponseSideInfo?>? wrapped)
        {
            if (enumerable is IAsyncEnumerable<T> typedEnumerable)
            {
                wrapped = new AsyncEnumerableWrapper(typedEnumerable, isDevelopment, unhandledExceptionCallback ?? DefaultUnhandledExceptionCallback, refreshInterval);
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
            if (enumerable is IEnumerable<T> typedEnumerable)
            {
                wrapped = new EnumerableWrapper(typedEnumerable, isDevelopment, unhandledExceptionCallback ?? DefaultUnhandledExceptionCallback);
                return true;
            }

            wrapped = null;
            return false;
        }

        private sealed class AsyncEnumerableWrapper : IAsyncEnumerable<T>
        {
            private readonly IAsyncEnumerable<T> _enumerable;
            private readonly bool _isDevelopment;
            private readonly Action<Exception> _unhandledExceptionCallback;
            private readonly TimeSpan? _refreshInterval;

            public AsyncEnumerableWrapper(IAsyncEnumerable<T> enumerable, bool isDevelopment, Action<Exception> unhandledExceptionCallback, TimeSpan? refreshInterval)
            {
                _enumerable = enumerable;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
                _refreshInterval = refreshInterval;
            }

            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                return new AsyncEnumeratorWrapper(_enumerable.GetAsyncEnumerator(cancellationToken), _isDevelopment, _unhandledExceptionCallback, _refreshInterval, cancellationToken);
            }
        }

        private sealed class AsyncEnumeratorWrapper : IAsyncEnumerator<T>
        {
            private IAsyncEnumerator<T>? _enumerator;
            private readonly bool _isDevelopment;
            private readonly Action<Exception> _unhandledExceptionCallback;
            private readonly TimeSpan? _refreshInterval;
            private readonly CancellationToken _cancellationToken;
            private Task<bool>? _pendingMoveNext;
            private T? _current;

            public AsyncEnumeratorWrapper(IAsyncEnumerator<T> enumerator, bool isDevelopment, Action<Exception> unhandledExceptionCallback, TimeSpan? refreshInterval, CancellationToken cancellationToken)
            {
                _enumerator = enumerator;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
                _refreshInterval = refreshInterval;
                _cancellationToken = cancellationToken;
            }

            public T Current => _current!;

            public async ValueTask DisposeAsync()
            {
                if (_enumerator != null)
                {
                    // Observe any pending MoveNextAsync before disposing the underlying enumerator.
                    if (_pendingMoveNext is { } pending)
                    {
                        _pendingMoveNext = null;

                        try
                        {
                            await pending;
                        }
                        catch { }
                    }

                    await _enumerator.DisposeAsync();
                    _enumerator = null;
                }

                _current = null;
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

                    if (_refreshInterval is not TimeSpan refreshInterval)
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

                    if (await Task.WhenAny(moveNextTask, Task.Delay(refreshInterval, _cancellationToken)) != moveNextTask)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();

                        // Timed out waiting for the next item: emit a ping (an item with null side info, ignored by the client) and keep waiting next time.
                        _pendingMoveNext = moveNextTask;
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
                    _enumerator = null;
                    _pendingMoveNext = null;
                    _ = (IStoresResponseSideInfo)_current;
                    return true;
                }
            }
        }

        private sealed class EnumerableWrapper : IEnumerable<T>
        {
            private readonly IEnumerable<T> _enumerable;
            private readonly bool _isDevelopment;
            private readonly Action<Exception> _unhandledExceptionCallback;

            public EnumerableWrapper(IEnumerable<T> enumerable, bool isDevelopment, Action<Exception> unhandledExceptionCallback)
            {
                _enumerable = enumerable;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
            }

            public IEnumerator<T> GetEnumerator()
            {
                return new EnumeratorWrapper(_enumerable.GetEnumerator(), _isDevelopment, _unhandledExceptionCallback);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        private sealed class EnumeratorWrapper : IEnumerator<T>
        {
            private IEnumerator<T>? _enumerator;
            private readonly bool _isDevelopment;
            private readonly Action<Exception> _unhandledExceptionCallback;
            private T? _current;

            public EnumeratorWrapper(IEnumerator<T> enumerator, bool isDevelopment, Action<Exception> unhandledExceptionCallback)
            {
                _enumerator = enumerator;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
            }

            public T Current => _current!;

            object IEnumerator.Current => Current!;

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
                    _enumerator = null;
                    _ = (IStoresResponseSideInfo)_current;
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
