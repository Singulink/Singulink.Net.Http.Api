namespace Singulink.Net.Http.Api.Client;

public static class CancellationAssertions
{
    /// <summary>
    /// Awaits a task that is expected to be cancelled, and returns its exception. Shouldly's ThrowAsync replaces the exception of a cancelled task with one of
    /// its own.
    /// </summary>
    public static async Task<OperationCanceledException> ShouldBeCancelledAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException ex)
        {
            return ex;
        }

        throw new ShouldAssertException("The task was not cancelled.");
    }
}
