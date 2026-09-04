using System.Runtime.CompilerServices;

namespace Singulink.Net.Http.Api;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Defines a fire-and-forget hub message with no arguments. Messages can be sent in either direction: from the server to clients (received via a client
/// handler) or from a client to the server (handled by a hub method with the same name).
/// </summary>
/// <remarks>
/// The message name defaults to the name of the member that the definition is assigned to (via <see cref="CallerMemberNameAttribute"/>), so definitions are
/// typically declared as static properties in a shared contract class, e.g. <c>public static HubMessage&lt;string&gt; Echo { get; } = new();</c>.
/// </remarks>
public sealed class HubMessage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HubMessage"/> class.
    /// </summary>
    /// <param name="name">The message name. Defaults to the name of the member the definition is assigned to.</param>
    public HubMessage([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>
    /// Gets the message name.
    /// </summary>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a fire-and-forget hub message with one argument.
/// </summary>
/// <typeparam name="T1">The type of the message argument.</typeparam>
/// <inheritdoc cref="HubMessage" path="/remarks"/>
public sealed class HubMessage<T1>
{
    /// <inheritdoc cref="HubMessage(string)"/>
    public HubMessage([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubMessage.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a fire-and-forget hub message with two arguments.
/// </summary>
/// <typeparam name="T1">The type of the first message argument.</typeparam>
/// <typeparam name="T2">The type of the second message argument.</typeparam>
/// <inheritdoc cref="HubMessage" path="/remarks"/>
public sealed class HubMessage<T1, T2>
{
    /// <inheritdoc cref="HubMessage(string)"/>
    public HubMessage([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubMessage.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a fire-and-forget hub message with three arguments.
/// </summary>
/// <typeparam name="T1">The type of the first message argument.</typeparam>
/// <typeparam name="T2">The type of the second message argument.</typeparam>
/// <typeparam name="T3">The type of the third message argument.</typeparam>
/// <inheritdoc cref="HubMessage" path="/remarks"/>
public sealed class HubMessage<T1, T2, T3>
{
    /// <inheritdoc cref="HubMessage(string)"/>
    public HubMessage([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubMessage.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// Defines a fire-and-forget hub message with four arguments.
/// </summary>
/// <typeparam name="T1">The type of the first message argument.</typeparam>
/// <typeparam name="T2">The type of the second message argument.</typeparam>
/// <typeparam name="T3">The type of the third message argument.</typeparam>
/// <typeparam name="T4">The type of the fourth message argument.</typeparam>
/// <inheritdoc cref="HubMessage" path="/remarks"/>
public sealed class HubMessage<T1, T2, T3, T4>
{
    /// <inheritdoc cref="HubMessage(string)"/>
    public HubMessage([CallerMemberName] string name = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc cref="HubMessage.Name"/>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
