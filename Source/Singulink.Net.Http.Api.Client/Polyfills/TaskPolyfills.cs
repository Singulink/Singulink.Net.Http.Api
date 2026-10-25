#if !NET

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Provides <see cref="Task{TResult}"/> members that .NET Standard 2.0 lacks, so the same source compiles for both builds.
/// </summary>
internal static class TaskPolyfills
{
    /// <summary>
    /// Waits for the task to complete or for the token to be cancelled, whichever happens first.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the task completed.</exception>
    public static async Task<TResult> WaitAsync<TResult>(this Task<TResult> task, CancellationToken cancellationToken)
    {
        if (!task.IsCompleted && cancellationToken.CanBeCanceled)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(cancellationToken);
            }
        }

        return await task.ConfigureAwait(false);
    }
}

#endif
