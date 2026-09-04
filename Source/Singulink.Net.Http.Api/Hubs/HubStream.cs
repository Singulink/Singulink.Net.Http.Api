using System.Runtime.CompilerServices;

namespace Singulink.Net.Http.Api;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Defines a hub method with no arguments that streams items from the server to the client. Handled on the server by a hub method with the same name that
/// returns <see cref="IAsyncEnumerable{T}"/> (or a channel reader) of the item type.
/// </summary>
/// <typeparam name="TItem">The type of the streamed items.</typeparam>
/// <remarks>
/// <para>
/// The stream name defaults to the name of the member that the definition is assigned to (via <see cref="CallerMemberNameAttribute"/>), so definitions are
/// typically declared as static properties in a shared contract class, e.g. <c>public static HubStream&lt;int, Update&gt; Updates { get; } = new();</c>.
/// </para>
/// <para>
/// Streaming from a client to the server does not require a separate definition: an <see cref="IAsyncEnumerable{T}"/> (or channel reader) argument of a
/// <see cref="HubMessage{T1}"/> or <see cref="HubMethod{T1, TResult}"/> is streamed to the server automatically.
/// </para>
/// </remarks>
public sealed class HubStream<TItem>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HubStream{TItem}"/> class.
    /// </summary>
    /// <param name="name">The stream name. Defaults to the name of the member the definition is assigned to.</param>
    public HubStream([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>
    /// Gets the stream name.
    /// </summary>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a hub method with one argument that streams items from the server to the client.
/// </summary>
/// <typeparam name="T1">The type of the method argument.</typeparam>
/// <typeparam name="TItem">The type of the streamed items.</typeparam>
/// <inheritdoc cref="HubStream{TItem}" path="/remarks"/>
public sealed class HubStream<T1, TItem>
{
    /// <inheritdoc cref="HubStream{TItem}(string)"/>
    public HubStream([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubStream{TItem}.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a hub method with two arguments that streams items from the server to the client.
/// </summary>
/// <typeparam name="T1">The type of the first method argument.</typeparam>
/// <typeparam name="T2">The type of the second method argument.</typeparam>
/// <typeparam name="TItem">The type of the streamed items.</typeparam>
/// <inheritdoc cref="HubStream{TItem}" path="/remarks"/>
public sealed class HubStream<T1, T2, TItem>
{
    /// <inheritdoc cref="HubStream{TItem}(string)"/>
    public HubStream([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubStream{TItem}.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a hub method with three arguments that streams items from the server to the client.
/// </summary>
/// <typeparam name="T1">The type of the first method argument.</typeparam>
/// <typeparam name="T2">The type of the second method argument.</typeparam>
/// <typeparam name="T3">The type of the third method argument.</typeparam>
/// <typeparam name="TItem">The type of the streamed items.</typeparam>
/// <inheritdoc cref="HubStream{TItem}" path="/remarks"/>
public sealed class HubStream<T1, T2, T3, TItem>
{
    /// <inheritdoc cref="HubStream{TItem}(string)"/>
    public HubStream([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubStream{TItem}.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
