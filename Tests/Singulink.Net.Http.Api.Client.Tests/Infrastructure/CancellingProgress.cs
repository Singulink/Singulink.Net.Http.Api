namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A progress that cancels an upload when a report matches a condition.
/// </summary>
public sealed class CancellingProgress(Func<UploadProgress, bool> condition, CancellationTokenSource cancellation) : IProgress<UploadProgress>
{
    public void Report(UploadProgress value)
    {
        if (condition(value))
            cancellation.Cancel();
    }
}
