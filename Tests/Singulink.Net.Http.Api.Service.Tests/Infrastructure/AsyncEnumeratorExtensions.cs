namespace Singulink.Net.Http.Api;

/// <summary>
/// Disposes the enumerators of tests that fail.
/// </summary>
internal static class AsyncEnumeratorExtensions
{
    /// <summary>
    /// Disposes an enumerator after its test has failed. An enumerator can't be disposed while a move is pending, such as after a timeout, so the exception
    /// of the disposal is ignored then, and the test reports its own failure.
    /// </summary>
    public static async Task DisposeAfterFailureAsync<T>(this IAsyncEnumerator<T> enumerator)
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (NotSupportedException)
        {
        }
    }
}
