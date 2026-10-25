namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The timeout of a chunk's request that runs while the request sends nothing.
/// </summary>
[PrefixTestClass]
public sealed class InactivityTimeoutTests
{
    [TestMethod]
    public void ResetAfterDispose_DoesNothing()
    {
        // A timer that throws when it is changed once it is disposed, as System.Threading.Timer does.
        var timeout = new InactivityTimeout(TimeSpan.FromMinutes(1), new DisposingTimeProvider(), CancellationToken.None);
        timeout.Dispose();

        Should.NotThrow(timeout.Reset);
    }

    private sealed class DisposingTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer();

        private sealed class Timer : ITimer
        {
            private bool _isDisposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => _isDisposed ? throw new ObjectDisposedException(nameof(Timer)) : true;

            public void Dispose() => _isDisposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
