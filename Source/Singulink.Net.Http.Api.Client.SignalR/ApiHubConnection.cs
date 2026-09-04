using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A hub connection that exposes a strongly typed API based on <see cref="HubMessage"/>, <see cref="HubMethod{TResult}"/> and <see cref="HubStream{TItem}"/>
/// definitions, and reports errors from the server as <see cref="ApiException"/> instances (matching the behavior of regular API requests).
/// </summary>
/// <remarks>
/// Errors are reported as <see cref="ApiException"/> instances when the server uses the API exception hub filter; other hub errors are reported as
/// <see cref="HubException"/> instances as usual. Errors that close the connection (e.g. thrown while connecting) are reported via <see cref="Closed"/>.
/// </remarks>
public sealed class ApiHubConnection : IAsyncDisposable
{
    private readonly HubConnection _connection;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiHubConnection"/> class that wraps the specified connection.
    /// </summary>
    public ApiHubConnection(HubConnection connection)
    {
        _connection = connection;
        _connection.Closed += error => InvokeHandlersAsync(Closed, Translate(error));
        _connection.Reconnecting += error => InvokeHandlersAsync(Reconnecting, Translate(error));
        _connection.Reconnected += connectionId => InvokeHandlersAsync(Reconnected, connectionId);
    }

    /// <summary>
    /// Occurs when the connection is closed. The exception (if any) that caused the connection to close is provided as the argument.
    /// </summary>
    public event Func<Exception?, Task>? Closed;

    /// <summary>
    /// Occurs when the connection starts reconnecting after losing its underlying connection. The exception (if any) that caused the connection to be lost is
    /// provided as the argument.
    /// </summary>
    public event Func<Exception?, Task>? Reconnecting;

    /// <summary>
    /// Occurs when the connection successfully reconnects. The new connection ID is provided as the argument.
    /// </summary>
    public event Func<string?, Task>? Reconnected;

    /// <summary>
    /// Gets the underlying <see cref="HubConnection"/>. Intended for advanced scenarios not covered by the typed API; errors from direct use of the underlying
    /// connection are not translated to <see cref="ApiException"/> instances.
    /// </summary>
    public HubConnection UnderlyingConnection => _connection;

    /// <summary>
    /// Gets the current state of the connection.
    /// </summary>
    public HubConnectionState State => _connection.State;

    /// <summary>
    /// Gets the connection ID, or <see langword="null"/> if the connection is not established.
    /// </summary>
    public string? ConnectionId => _connection.ConnectionId;

    /// <summary>
    /// Starts the connection.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => TranslateAsync(_connection.StartAsync(cancellationToken));

    /// <summary>
    /// Stops the connection.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => _connection.StopAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    // Handlers for messages sent by the server:

    /// <summary>
    /// Registers a handler that is invoked when the server sends the message.
    /// </summary>
    public IDisposable On(HubMessage message, Action handler) => _connection.On(message.Name, handler);

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On(HubMessage message, Func<Task> handler) => _connection.On(message.Name, handler);

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1>(HubMessage<T1> message, Action<T1> handler) => _connection.On(message.Name, handler);

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1>(HubMessage<T1> message, Func<T1, Task> handler) => _connection.On(message.Name, handler);

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2>(HubMessage<T1, T2> message, Action<T1, T2> handler) => _connection.On(message.Name, handler);

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2>(HubMessage<T1, T2> message, Func<T1, T2, Task> handler) => _connection.On(message.Name, handler);

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2, T3>(HubMessage<T1, T2, T3> message, Action<T1, T2, T3> handler) => _connection.On(message.Name, handler);

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2, T3>(HubMessage<T1, T2, T3> message, Func<T1, T2, T3, Task> handler) => _connection.On(message.Name, handler);

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2, T3, T4>(HubMessage<T1, T2, T3, T4> message, Action<T1, T2, T3, T4> handler) => _connection.On(message.Name, handler);

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2, T3, T4>(HubMessage<T1, T2, T3, T4> message, Func<T1, T2, T3, T4, Task> handler) => _connection.On(message.Name, handler);

    // Handlers for methods invoked by the server (client results):

    /// <summary>
    /// Registers a handler that is invoked when the server invokes the method, returning the result to the server.
    /// </summary>
    public IDisposable On<TResult>(HubMethod<TResult> method, Func<TResult> handler) => _connection.On(method.Name, handler);

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<TResult>(HubMethod<TResult> method, Func<Task<TResult>> handler) => _connection.On(method.Name, handler);

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, TResult>(HubMethod<T1, TResult> method, Func<T1, TResult> handler) => _connection.On(method.Name, handler);

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, TResult>(HubMethod<T1, TResult> method, Func<T1, Task<TResult>> handler) => _connection.On(method.Name, handler);

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, TResult>(HubMethod<T1, T2, TResult> method, Func<T1, T2, TResult> handler) => _connection.On(method.Name, handler);

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, TResult>(HubMethod<T1, T2, TResult> method, Func<T1, T2, Task<TResult>> handler) => _connection.On(method.Name, handler);

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, T3, TResult>(HubMethod<T1, T2, T3, TResult> method, Func<T1, T2, T3, TResult> handler)
        => _connection.On(method.Name, handler);

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, T3, TResult>(HubMethod<T1, T2, T3, TResult> method, Func<T1, T2, T3, Task<TResult>> handler)
        => _connection.On(method.Name, handler);

    // Sending messages to the server (without waiting for the server to process them):

    /// <summary>
    /// Sends a message to the server without waiting for the server to process it.
    /// </summary>
    public Task SendAsync(HubMessage message, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [], cancellationToken));

    /// <inheritdoc cref="SendAsync(HubMessage, CancellationToken)"/>
    public Task SendAsync<T1>(HubMessage<T1> message, T1 arg1, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [arg1], cancellationToken));

    /// <inheritdoc cref="SendAsync(HubMessage, CancellationToken)"/>
    public Task SendAsync<T1, T2>(HubMessage<T1, T2> message, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [arg1, arg2], cancellationToken));

    /// <inheritdoc cref="SendAsync(HubMessage, CancellationToken)"/>
    public Task SendAsync<T1, T2, T3>(HubMessage<T1, T2, T3> message, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [arg1, arg2, arg3], cancellationToken));

    /// <inheritdoc cref="SendAsync(HubMessage, CancellationToken)"/>
    public Task SendAsync<T1, T2, T3, T4>(HubMessage<T1, T2, T3, T4> message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [arg1, arg2, arg3, arg4], cancellationToken));

    // Invoking messages on the server (waiting for the server to finish processing them):

    /// <summary>
    /// Sends a message to the server and waits for the server to finish processing it.
    /// </summary>
    public Task InvokeAsync(HubMessage message, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [], cancellationToken));

    /// <inheritdoc cref="InvokeAsync(HubMessage, CancellationToken)"/>
    public Task InvokeAsync<T1>(HubMessage<T1> message, T1 arg1, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [arg1], cancellationToken));

    /// <inheritdoc cref="InvokeAsync(HubMessage, CancellationToken)"/>
    public Task InvokeAsync<T1, T2>(HubMessage<T1, T2> message, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [arg1, arg2], cancellationToken));

    /// <inheritdoc cref="InvokeAsync(HubMessage, CancellationToken)"/>
    public Task InvokeAsync<T1, T2, T3>(HubMessage<T1, T2, T3> message, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [arg1, arg2, arg3], cancellationToken));

    /// <inheritdoc cref="InvokeAsync(HubMessage, CancellationToken)"/>
    public Task InvokeAsync<T1, T2, T3, T4>(HubMessage<T1, T2, T3, T4> message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [arg1, arg2, arg3, arg4], cancellationToken));

    // Invoking methods on the server:

    /// <summary>
    /// Invokes a method on the server and returns its result.
    /// </summary>
    public Task<TResult> InvokeAsync<TResult>(HubMethod<TResult> method, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync<TResult>(method.Name, [], cancellationToken));

    /// <inheritdoc cref="InvokeAsync{TResult}(HubMethod{TResult}, CancellationToken)"/>
    public Task<TResult> InvokeAsync<T1, TResult>(HubMethod<T1, TResult> method, T1 arg1, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync<TResult>(method.Name, [arg1], cancellationToken));

    /// <inheritdoc cref="InvokeAsync{TResult}(HubMethod{TResult}, CancellationToken)"/>
    public Task<TResult> InvokeAsync<T1, T2, TResult>(HubMethod<T1, T2, TResult> method, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync<TResult>(method.Name, [arg1, arg2], cancellationToken));

    /// <inheritdoc cref="InvokeAsync{TResult}(HubMethod{TResult}, CancellationToken)"/>
    public Task<TResult> InvokeAsync<T1, T2, T3, TResult>(
        HubMethod<T1, T2, T3, TResult> method, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync<TResult>(method.Name, [arg1, arg2, arg3], cancellationToken));

    // Streaming from the server:

    /// <summary>
    /// Invokes a streaming method on the server and returns the streamed items. The stream is started when enumeration begins and is cancelled when the
    /// enumeration is disposed or the cancellation token is cancelled.
    /// </summary>
    public IAsyncEnumerable<TItem> StreamAsync<TItem>(HubStream<TItem> stream, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.StreamAsyncCore<TItem>(stream.Name, [], cancellationToken), cancellationToken);

    /// <inheritdoc cref="StreamAsync{TItem}(HubStream{TItem}, CancellationToken)"/>
    public IAsyncEnumerable<TItem> StreamAsync<T1, TItem>(HubStream<T1, TItem> stream, T1 arg1, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.StreamAsyncCore<TItem>(stream.Name, [arg1], cancellationToken), cancellationToken);

    /// <inheritdoc cref="StreamAsync{TItem}(HubStream{TItem}, CancellationToken)"/>
    public IAsyncEnumerable<TItem> StreamAsync<T1, T2, TItem>(HubStream<T1, T2, TItem> stream, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.StreamAsyncCore<TItem>(stream.Name, [arg1, arg2], cancellationToken), cancellationToken);

    /// <inheritdoc cref="StreamAsync{TItem}(HubStream{TItem}, CancellationToken)"/>
    public IAsyncEnumerable<TItem> StreamAsync<T1, T2, T3, TItem>(
        HubStream<T1, T2, T3, TItem> stream, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.StreamAsyncCore<TItem>(stream.Name, [arg1, arg2, arg3], cancellationToken), cancellationToken);

    /// <summary>
    /// Translates a hub error into the corresponding <see cref="ApiException"/> if it represents one, otherwise returns the exception unchanged.
    /// </summary>
    private static Exception? Translate(Exception? exception)
    {
        if (exception is HubException hubException && ResponseExceptionInfo.TryCreateFromHubError(hubException.Message) is { } apiException)
            return apiException;

        return exception;
    }

    private static async Task TranslateAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (HubException ex) when (ResponseExceptionInfo.TryCreateFromHubError(ex.Message) is { } apiException)
        {
            throw apiException;
        }
    }

    private static async Task<T> TranslateAsync<T>(Task<T> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (HubException ex) when (ResponseExceptionInfo.TryCreateFromHubError(ex.Message) is { } apiException)
        {
            throw apiException;
        }
    }

    private static async IAsyncEnumerable<T> TranslateAsync<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;

                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (HubException ex)
                {
                    if (ResponseExceptionInfo.TryCreateFromHubError(ex.Message) is { } apiException)
                        throw apiException;

                    ExceptionDispatchInfo.Throw(ex);
                    throw; // Unreachable
                }

                if (!hasNext)
                    yield break;

                yield return enumerator.Current;
            }
        }
    }

    private static Task InvokeHandlersAsync<T>(Func<T, Task>? handlers, T arg)
    {
        if (handlers is null)
            return Task.CompletedTask;

        var invocationList = handlers.GetInvocationList();

        if (invocationList.Length is 1)
            return handlers(arg);

        var tasks = new Task[invocationList.Length];

        for (int i = 0; i < invocationList.Length; i++)
            tasks[i] = ((Func<T, Task>)invocationList[i])(arg);

        return Task.WhenAll(tasks);
    }
}
