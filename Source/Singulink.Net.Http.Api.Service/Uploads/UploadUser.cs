namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Gets the user that a request of a chunked upload is made for, from the session token type that chunked uploads are registered with.
/// </summary>
internal abstract class UploadUser
{
    /// <summary>
    /// Gets the ID of the request's signed-in user.
    /// </summary>
    /// <exception cref="UnauthorizedApiException">The request has no session (<see cref="ApiErrorCodes.SignInRequired"/>).</exception>
    public abstract ValueTask<string> GetUserIdAsync(HttpContext context);

    /// <summary>
    /// Checks that session handling is registered for the session token type.
    /// </summary>
    /// <exception cref="InvalidOperationException">Session handling is not registered for the session token type.</exception>
    public abstract void EnsureSessionHandling(IServiceProvider services);
}

/// <inheritdoc/>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
internal sealed class UploadUser<TSessionToken> : UploadUser
    where TSessionToken : class, ISessionToken
{
    public override async ValueTask<string> GetUserIdAsync(HttpContext context)
    {
        var token = await context.GetRequiredSessionTokenAsync<TSessionToken>();
        return token.UserId;
    }

    public override void EnsureSessionHandling(IServiceProvider services)
    {
        if (services.GetService(typeof(ISessionContextFactory<TSessionToken>)) is null)
        {
            throw new InvalidOperationException(
                $"Chunked uploads are registered for the session token type '{typeof(TSessionToken)}', but session handling is not registered for it. " +
                "Register it with AddHttpSessionHandling.");
        }
    }
}
