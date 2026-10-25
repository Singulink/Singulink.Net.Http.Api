namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A progress that records the values reported to it as they are reported, on the reporting thread.
/// </summary>
public sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly List<T> _reports = [];

    public IReadOnlyList<T> Reports
    {
        get {
            lock (_reports)
                return [.. _reports];
        }
    }

    public void Report(T value)
    {
        lock (_reports)
            _reports.Add(value);
    }
}
