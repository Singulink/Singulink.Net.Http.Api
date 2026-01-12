namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Attribute for configuring options when binding session tokens from HTTP contexts.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public class SessionAccessAttribute : Attribute
{
    /// <summary>
    /// Gets options for configuring session access behavior.
    /// </summary>
    public SessionAccessOptions Options { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionAccessAttribute"/> class.
    /// </summary>
    /// <param name="options">Options for configuring session access behavior.</param>
    public SessionAccessAttribute(SessionAccessOptions options)
    {
        Options = options;
    }
}
