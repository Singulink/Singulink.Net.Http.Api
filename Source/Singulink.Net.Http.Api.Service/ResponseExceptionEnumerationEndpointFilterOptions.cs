using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Options for configuring the <see cref="ResponseExceptionEnumerationEndpointFilter" /> to handle API exceptions for enumerable results.
/// </summary>
public sealed class ResponseExceptionEnumerationEndpointFilterOptions
{
    /// <summary>
    /// Adds a new enumerable type to the list of types to handle for enumeration.
    /// </summary>
    public ResponseExceptionEnumerationEndpointFilterOptions Add<T>()
        where T : class, ISupportsResponseException<T>
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
    public ResponseExceptionEnumerationEndpointFilterOptions AddExceptionObserver(Action<Exception> callback)
    {
        _unhandledExceptionCallback += callback;
        return this;
    }

    internal Action<Exception>? _unhandledExceptionCallback;

    internal readonly List<HelperBase> _enumerableTypes = [];

    internal abstract class HelperBase
    {
        public abstract bool TryWrap(
            IAsyncEnumerable<ISupportsResponseException?> enumerable,
            bool isDevelopment,
            Action<Exception>? unhandledExceptionCallback,
            [NotNullWhen(true)] out IAsyncEnumerable<ISupportsResponseException?>? wrapped);

        public abstract bool TryWrap(
            IEnumerable<ISupportsResponseException?> enumerable,
            bool isDevelopment,
            Action<Exception>? unhandledExceptionCallback,
            [NotNullWhen(true)] out IEnumerable<ISupportsResponseException?>? wrapped);
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
        where T : class, ISupportsResponseException<T>
    {
        public override bool TryWrap(
            IAsyncEnumerable<ISupportsResponseException?> enumerable,
            bool isDevelopment,
            Action<Exception>? unhandledExceptionCallback,
            [NotNullWhen(true)] out IAsyncEnumerable<ISupportsResponseException?>? wrapped)
        {
            if (enumerable is IAsyncEnumerable<T> typedEnumerable)
            {
                wrapped = new AsyncEnumerableWrapper(typedEnumerable, isDevelopment, unhandledExceptionCallback ?? DefaultUnhandledExceptionCallback);
                return true;
            }

            wrapped = null;
            return false;
        }

        public override bool TryWrap(
            IEnumerable<ISupportsResponseException?> enumerable,
            bool isDevelopment,
            Action<Exception>? unhandledExceptionCallback,
            [NotNullWhen(true)] out IEnumerable<ISupportsResponseException?>? wrapped)
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

            public AsyncEnumerableWrapper(IAsyncEnumerable<T> enumerable, bool isDevelopment, Action<Exception> unhandledExceptionCallback)
            {
                _enumerable = enumerable;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
            }

            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                return new AsyncEnumeratorWrapper(_enumerable.GetAsyncEnumerator(cancellationToken), _isDevelopment, _unhandledExceptionCallback);
            }
        }

        private sealed class AsyncEnumeratorWrapper : IAsyncEnumerator<T>
        {
            private IAsyncEnumerator<T>? _enumerator;
            private readonly bool _isDevelopment;
            private readonly Action<Exception> _unhandledExceptionCallback;
            private T? _current;

            public AsyncEnumeratorWrapper(IAsyncEnumerator<T> enumerator, bool isDevelopment, Action<Exception> unhandledExceptionCallback)
            {
                _enumerator = enumerator;
                _isDevelopment = isDevelopment;
                _unhandledExceptionCallback = unhandledExceptionCallback;
            }

            public T Current => _current!;

            public async ValueTask DisposeAsync()
            {
                if (_enumerator != null)
                {
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
                    bool hasNext = await _enumerator.MoveNextAsync();
                    _current = hasNext ? _enumerator.Current : null;
                    return hasNext;
                }
                catch (Exception ex)
                {
                    _unhandledExceptionCallback(ex);
                    _current = T.CreateResponseExceptionValue(ResponseExceptionInfo.FromException(ex, _isDevelopment).ToEnumerationString());
                    _enumerator = null;
                    _ = (IStoresResponseException)_current;
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
                    _current = T.CreateResponseExceptionValue(ResponseExceptionInfo.FromException(ex, _isDevelopment).ToEnumerationString());
                    _enumerator = null;
                    _ = (IStoresResponseException)_current;
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
