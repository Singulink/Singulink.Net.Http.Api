using System.Runtime.CompilerServices;

namespace Singulink.Net.Http.Api;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Defines a hub method with no arguments that returns a result. Methods can be invoked in either direction: by a client on the server (handled by a hub
/// method with the same name) or by the server on a specific client (handled by a client handler that returns the result).
/// </summary>
/// <typeparam name="TResult">The type of the result.</typeparam>
/// <remarks>
/// The method name defaults to the name of the member that the definition is assigned to (via <see cref="CallerMemberNameAttribute"/>), so definitions are
/// typically declared as static properties in a shared contract class, e.g. <c>public static HubMethod&lt;int, int&gt; Double { get; } = new();</c>.
/// </remarks>
public sealed class HubMethod<TResult>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HubMethod{TResult}"/> class.
    /// </summary>
    /// <param name="name">The method name. Defaults to the name of the member the definition is assigned to.</param>
    public HubMethod([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>
    /// Gets the method name.
    /// </summary>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a hub method with one argument that returns a result.
/// </summary>
/// <typeparam name="T1">The type of the method argument.</typeparam>
/// <typeparam name="TResult">The type of the result.</typeparam>
/// <inheritdoc cref="HubMethod{TResult}" path="/remarks"/>
public sealed class HubMethod<T1, TResult>
{
    /// <inheritdoc cref="HubMethod{TResult}(string)"/>
    public HubMethod([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubMethod{TResult}.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a hub method with two arguments that returns a result.
/// </summary>
/// <typeparam name="T1">The type of the first method argument.</typeparam>
/// <typeparam name="T2">The type of the second method argument.</typeparam>
/// <typeparam name="TResult">The type of the result.</typeparam>
/// <inheritdoc cref="HubMethod{TResult}" path="/remarks"/>
public sealed class HubMethod<T1, T2, TResult>
{
    /// <inheritdoc cref="HubMethod{TResult}(string)"/>
    public HubMethod([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubMethod{TResult}.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a hub method with three arguments that returns a result.
/// </summary>
/// <typeparam name="T1">The type of the first method argument.</typeparam>
/// <typeparam name="T2">The type of the second method argument.</typeparam>
/// <typeparam name="T3">The type of the third method argument.</typeparam>
/// <typeparam name="TResult">The type of the result.</typeparam>
/// <inheritdoc cref="HubMethod{TResult}" path="/remarks"/>
public sealed class HubMethod<T1, T2, T3, TResult>
{
    /// <inheritdoc cref="HubMethod{TResult}(string)"/>
    public HubMethod([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubMethod{TResult}.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
