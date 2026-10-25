using System.Reflection;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents an application's session token, which session handling keeps in the session cookie.
/// </summary>
public interface ISessionToken
{
    /// <summary>
    /// Gets the ID of the user the session belongs to, as a string. The user ID precondition of requests is compared with it.
    /// </summary>
    string UserId { get; }
}

/// <summary>
/// Represents an application's session token that endpoints take as a parameter. Minimal API endpoints bind a parameter of the token type with
/// <see cref="HttpContextExtensions.BindSessionTokenAsync{TSessionToken}(HttpContext, ParameterInfo)"/>, which reads the parameter's nullability and its
/// <see cref="SessionAccessAttribute"/>.
/// </summary>
/// <typeparam name="TSelf">The session token type.</typeparam>
public interface ISessionToken<TSelf> : ISessionToken, IBindableFromHttpContext<TSelf>
    where TSelf : class, ISessionToken<TSelf>
{
    /// <summary>
    /// Binds the session token parameter of an endpoint with <see cref="HttpContextExtensions.BindSessionTokenAsync{TSessionToken}(HttpContext,
    /// ParameterInfo)"/>.
    /// </summary>
    static ValueTask<TSelf?> IBindableFromHttpContext<TSelf>.BindAsync(HttpContext context, ParameterInfo parameter)
    {
        return context.BindSessionTokenAsync<TSelf>(parameter);
    }
}
