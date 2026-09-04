using Microsoft.AspNetCore.SignalR;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for sending <see cref="HubMessage"/> definitions and invoking <see cref="HubMethod{TResult}"/> definitions on hub clients.
/// </summary>
public static class HubClientProxyExtensions
{
    /// <summary>
    /// Sends a message to the clients.
    /// </summary>
    public static Task SendAsync(this IClientProxy clients, HubMessage message, CancellationToken cancellationToken = default)
    {
        return clients.SendCoreAsync(message.Name, [], cancellationToken);
    }

    /// <summary>
    /// Sends a message with the specified argument to the clients.
    /// </summary>
    public static Task SendAsync<T1>(this IClientProxy clients, HubMessage<T1> message, T1 arg1, CancellationToken cancellationToken = default)
    {
        return clients.SendCoreAsync(message.Name, [arg1], cancellationToken);
    }

    /// <summary>
    /// Sends a message with the specified arguments to the clients.
    /// </summary>
    public static Task SendAsync<T1, T2>(this IClientProxy clients, HubMessage<T1, T2> message, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
    {
        return clients.SendCoreAsync(message.Name, [arg1, arg2], cancellationToken);
    }

    /// <summary>
    /// Sends a message with the specified arguments to the clients.
    /// </summary>
    public static Task SendAsync<T1, T2, T3>(
        this IClientProxy clients, HubMessage<T1, T2, T3> message, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
    {
        return clients.SendCoreAsync(message.Name, [arg1, arg2, arg3], cancellationToken);
    }

    /// <summary>
    /// Sends a message with the specified arguments to the clients.
    /// </summary>
    public static Task SendAsync<T1, T2, T3, T4>(
        this IClientProxy clients, HubMessage<T1, T2, T3, T4> message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, CancellationToken cancellationToken = default)
    {
        return clients.SendCoreAsync(message.Name, [arg1, arg2, arg3, arg4], cancellationToken);
    }

    /// <summary>
    /// Invokes a method on the client and returns its result.
    /// </summary>
    public static Task<TResult> InvokeAsync<TResult>(this ISingleClientProxy client, HubMethod<TResult> method, CancellationToken cancellationToken = default)
    {
        return client.InvokeCoreAsync<TResult>(method.Name, [], cancellationToken);
    }

    /// <summary>
    /// Invokes a method with the specified argument on the client and returns its result.
    /// </summary>
    public static Task<TResult> InvokeAsync<T1, TResult>(
        this ISingleClientProxy client, HubMethod<T1, TResult> method, T1 arg1, CancellationToken cancellationToken = default)
    {
        return client.InvokeCoreAsync<TResult>(method.Name, [arg1], cancellationToken);
    }

    /// <summary>
    /// Invokes a method with the specified arguments on the client and returns its result.
    /// </summary>
    public static Task<TResult> InvokeAsync<T1, T2, TResult>(
        this ISingleClientProxy client, HubMethod<T1, T2, TResult> method, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
    {
        return client.InvokeCoreAsync<TResult>(method.Name, [arg1, arg2], cancellationToken);
    }

    /// <summary>
    /// Invokes a method with the specified arguments on the client and returns its result.
    /// </summary>
    public static Task<TResult> InvokeAsync<T1, T2, T3, TResult>(
        this ISingleClientProxy client, HubMethod<T1, T2, T3, TResult> method, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
    {
        return client.InvokeCoreAsync<TResult>(method.Name, [arg1, arg2, arg3], cancellationToken);
    }
}
