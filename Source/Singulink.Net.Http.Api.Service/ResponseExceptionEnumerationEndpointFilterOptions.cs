using System.Collections;
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

    internal readonly List<HelperBase> _enumerableTypes = [];

    internal abstract class HelperBase
    {
        public abstract bool TryWrap(
            IAsyncEnumerable<ISupportsResponseException?> enumerable,
            bool isDevelopment,
            [NotNullWhen(true)] out IAsyncEnumerable<ISupportsResponseException?>? wrapped);

        public abstract bool TryWrap(
            IEnumerable<ISupportsResponseException?> enumerable,
            bool isDevelopment,
            [NotNullWhen(true)] out IEnumerable<ISupportsResponseException?>? wrapped);
    }

    private sealed class Helper<T> : HelperBase
        where T : class, ISupportsResponseException<T>
    {
        public override bool TryWrap(
            IAsyncEnumerable<ISupportsResponseException?> enumerable,
            bool isDevelopment,
            [NotNullWhen(true)] out IAsyncEnumerable<ISupportsResponseException?>? wrapped)
        {
            if (enumerable is IAsyncEnumerable<T> typedEnumerable)
            {
                wrapped = new AsyncEnumerableWrapper(typedEnumerable, isDevelopment);
                return true;
            }

            wrapped = null;
            return false;
        }

        public override bool TryWrap(
            IEnumerable<ISupportsResponseException?> enumerable,
            bool isDevelopment,
            [NotNullWhen(true)] out IEnumerable<ISupportsResponseException?>? wrapped)
        {
            if (enumerable is IEnumerable<T> typedEnumerable)
            {
                wrapped = new EnumerableWrapper(typedEnumerable, isDevelopment);
                return true;
            }

            wrapped = null;
            return false;
        }

        private sealed class AsyncEnumerableWrapper : IAsyncEnumerable<T>
        {
            private readonly IAsyncEnumerable<T> _enumerable;
            private readonly bool _isDevelopment;

            public AsyncEnumerableWrapper(IAsyncEnumerable<T> enumerable, bool isDevelopment)
            {
                _enumerable = enumerable;
                _isDevelopment = isDevelopment;
            }

            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                return new AsyncEnumeratorWrapper(_enumerable.GetAsyncEnumerator(cancellationToken), _isDevelopment);
            }
        }

        private sealed class AsyncEnumeratorWrapper : IAsyncEnumerator<T>
        {
            private IAsyncEnumerator<T>? _enumerator;
            private readonly bool _isDevelopment;
            private T? _current;

            public AsyncEnumeratorWrapper(IAsyncEnumerator<T> enumerator, bool isDevelopment)
            {
                _enumerator = enumerator;
                _isDevelopment = isDevelopment;
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
                    _current = T.CreateResponseExceptionValue(ResponseExceptionInfo.FromException(ex, _isDevelopment));
                    _enumerator = null;
                    return true;
                }
            }
        }

        private sealed class EnumerableWrapper : IEnumerable<T>
        {
            private readonly IEnumerable<T> _enumerable;
            private readonly bool _isDevelopment;

            public EnumerableWrapper(IEnumerable<T> enumerable, bool isDevelopment)
            {
                _enumerable = enumerable;
                _isDevelopment = isDevelopment;
            }

            public IEnumerator<T> GetEnumerator()
            {
                return new EnumeratorWrapper(_enumerable.GetEnumerator(), _isDevelopment);
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
            private T? _current;

            public EnumeratorWrapper(IEnumerator<T> enumerator, bool isDevelopment)
            {
                _enumerator = enumerator;
                _isDevelopment = isDevelopment;
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
                    _current = T.CreateResponseExceptionValue(ResponseExceptionInfo.FromException(ex, _isDevelopment));
                    _enumerator = null;
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
