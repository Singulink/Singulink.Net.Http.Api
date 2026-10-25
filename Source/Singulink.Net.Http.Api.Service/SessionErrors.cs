namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Creates the exceptions that session contexts throw, so that each session context implementation of the toolkit reports the same errors.
/// </summary>
internal static class SessionErrors
{
    /// <summary>
    /// Creates the exception for a request whose origin is not allowed.
    /// </summary>
    public static ForbiddenApiException OriginBlocked()
    {
        return new ForbiddenApiException("The cross-origin request was blocked.") { ErrorCode = ApiErrorCodes.OriginBlocked };
    }

    /// <summary>
    /// Creates the exception for a request that requires a session and has none.
    /// </summary>
    public static UnauthorizedApiException SignInRequired()
    {
        return new UnauthorizedApiException("The user is not signed in.") { ErrorCode = ApiErrorCodes.SignInRequired };
    }

    /// <summary>
    /// Creates the exception for a request without the user ID precondition that it requires.
    /// </summary>
    public static UserRequiredApiException UserIdPreconditionMissing(string queryName)
    {
        return new UserRequiredApiException($"The request is missing the required '{queryName}' query parameter.");
    }

    /// <summary>
    /// Creates the exception for a request whose user ID precondition is not the user of the session. It carries the precondition as its
    /// <see cref="UserChangedApiException.ExpectedUserId"/>, which the error that the client gets doesn't include, and nothing about the session's user.
    /// </summary>
    public static UserChangedApiException UserIdPreconditionMismatch(string queryName, string userIdPrecondition)
    {
        return new UserChangedApiException($"The user in the '{queryName}' query parameter is not the user of the session.") {
            ExpectedUserId = userIdPrecondition,
        };
    }

    /// <summary>
    /// Creates the exception for setting the session token of a request that has no session.
    /// </summary>
    public static InvalidOperationException NoSessionToSetTokenOf()
    {
        return new InvalidOperationException("The request has no session to set the token of. Sign in to start a session.");
    }
}
