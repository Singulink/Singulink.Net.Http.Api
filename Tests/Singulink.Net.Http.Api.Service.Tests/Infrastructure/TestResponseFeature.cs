using Microsoft.AspNetCore.Http.Features;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Response feature that captures <c>OnStarting</c> callbacks (the default in-memory feature ignores them) and lets a test start the response so deferred
/// session token updates run.
/// </summary>
public sealed class TestResponseFeature : HttpResponseFeature
{
    private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];
    private bool _hasStarted;

    public override bool HasStarted => _hasStarted;

    public int OnStartingCallbackCount => _onStarting.Count;

    public override void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));

    /// <summary>
    /// Marks the response as started without running callbacks (simulates a token read that happens after the response body has begun).
    /// </summary>
    public void MarkStarted() => _hasStarted = true;

    /// <summary>
    /// Runs the registered callbacks (in reverse registration order, like Kestrel) and then marks the response as started.
    /// </summary>
    public async Task StartAsync()
    {
        if (_hasStarted)
            throw new InvalidOperationException("Response has already started.");

        for (int i = _onStarting.Count - 1; i >= 0; i--)
            await _onStarting[i].Callback(_onStarting[i].State);

        _hasStarted = true;
    }
}
