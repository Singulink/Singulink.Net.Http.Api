#if !NET

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Provides the cancellable <see cref="HttpContent"/> read overloads that .NET Standard 2.0 lacks, so the same source compiles for both builds, and the
/// workarounds that .NET Framework's response streams need.
/// </summary>
internal static class HttpContentPolyfills
{
    /// <summary>
    /// Reads the content as a string. Cancellation disposes the content to abort the read (see
    /// <see cref="WaitOrAbortAsync{T}(Task{T}, IDisposable, CancellationToken)"/>).
    /// </summary>
    public static Task<string> ReadAsStringAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return content.ReadAsStringAsync().WaitOrAbortAsync(content, cancellationToken);
    }

    /// <summary>
    /// Gets the content stream. Cancellation is only observed before the stream is opened.
    /// </summary>
    public static Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return content.ReadAsStreamAsync();
    }

    /// <summary>
    /// Waits for a read of a response to complete, or throws an <see cref="OperationCanceledException"/> and disposes the response or its content to abort
    /// the read when the token is cancelled first. The response streams of .NET Framework ignore cancellation once a read has started.
    /// </summary>
    /// <param name="readTask">The read.</param>
    /// <param name="owner">The response or content that the read is from, which is disposed off the calling thread (see
    /// <see cref="DisposeInBackground(IDisposable)"/>).</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    public static async Task<T> WaitOrAbortAsync<T>(this Task<T> readTask, IDisposable owner, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || readTask.IsCompleted)
            return await readTask.ConfigureAwait(false);

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
        {
            if (await Task.WhenAny(readTask, cancelled.Task).ConfigureAwait(false) == readTask)
                return await readTask.ConfigureAwait(false);
        }

        owner.DisposeInBackground();
        ObserveFailure(readTask);

        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>
    /// Disposes the response, content or stream on the thread pool. On .NET Framework, disposing a response whose body has not been read to the end blocks
    /// for about half a second.
    /// </summary>
    public static void DisposeInBackground(this IDisposable disposable)
    {
        ObserveFailure(Task.Run(disposable.Dispose));
    }

    /// <summary>
    /// Observes the exception of a task that is not awaited, so that it is not reported as unobserved.
    /// </summary>
    private static void ObserveFailure(Task task)
    {
        _ = task.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

#endif
