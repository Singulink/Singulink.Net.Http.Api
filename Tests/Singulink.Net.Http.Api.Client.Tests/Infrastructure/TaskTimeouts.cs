namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Gives the tasks that tests wait for a timeout, so that a test whose task never completes fails rather than hangs, also on .NET Framework, which has no
/// <c>Task.WaitAsync</c>.
/// </summary>
public static class TaskTimeouts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Waits for the task, and throws a <see cref="TimeoutException"/> if it does not complete within 20 seconds.
    /// </summary>
    public static async Task<T> WithTimeoutAsync<T>(this Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(Timeout)).ConfigureAwait(false) != task)
            throw new TimeoutException("The task did not complete in time.");

        return await task.ConfigureAwait(false);
    }
}
