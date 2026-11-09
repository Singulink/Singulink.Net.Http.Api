using Singulink.Enums;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for session handling: the session cookie, when sessions are validated and refreshed, and when they expire.
/// </summary>
/// <remarks>
/// Session handling checks the options when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class SessionHandlingOptions
{
    internal const int DefaultMaxDeviceLength = 512;

    /// <summary>
    /// Gets or sets the name of the cookie that holds the encrypted session. Default is <c>"__Host-session-token"</c>.
    /// </summary>
    /// <remarks>
    /// Browsers only accept a cookie whose name starts with <c>__Host-</c> from a secure origin, without a domain and with the path <c>/</c>, so such a name
    /// requires <see cref="CookieDomain"/> to be <see langword="null"/> and <see cref="CookiePath"/> to be <c>/</c>, which is checked when the application
    /// starts.
    /// </remarks>
    public string SessionCookieName {
        get;
        set {
            EnsureNotFrozen();
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = "__Host-session-token";

    /// <summary>
    /// Gets or sets the domain that the session cookie applies to, or <see langword="null"/> for the host that issued it. Default is
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A parent domain, such as <c>".example.com"</c>, shares the cookie with its subdomains. The applications that read it then have the same value and
    /// the same data protection key ring. A cookie with a domain cannot have a <c>__Host-</c> name (see <see cref="SessionCookieName"/>).
    /// </remarks>
    public string? CookieDomain {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the path that the session cookie applies to. Default is <c>"/"</c>.
    /// </summary>
    /// <remarks>
    /// A cookie with another path cannot have a <c>__Host-</c> name (see <see cref="SessionCookieName"/>).
    /// </remarks>
    public string CookiePath {
        get;
        set {
            EnsureNotFrozen();
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = "/";

    /// <summary>
    /// Gets or sets the purpose string that the session cookie is protected with (see
    /// <see cref="Microsoft.AspNetCore.DataProtection.IDataProtectionProvider.CreateProtector(string)"/>), or <see langword="null"/> for the default
    /// purpose: <c>Singulink/Session[</c>, the full name of the session token type, and <c>]</c>. Default is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A session cookie can only be read with the purpose it was protected with, so a different purpose signs everyone out. With the default purpose, that
    /// includes renaming the session token type or moving it to another namespace. Setting the purpose to the previous default purpose keeps reading the
    /// existing cookies.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is empty.</exception>
    public string? DataProtectionPurpose {
        get;
        set {
            EnsureNotFrozen();
            if (value is not null)
                ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the query parameter name for the user ID precondition. Default is <c>"if-userId"</c>.
    /// </summary>
    public string UserIdPreconditionQueryName {
        get;
        set {
            EnsureNotFrozen();
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = "if-userId";

    /// <summary>
    /// Gets or sets the maximum length of the device that session handling records for a session (see <see cref="SessionContext{TSessionToken}.Device"/>).
    /// Must be at least 1. Default is 512.
    /// </summary>
    /// <remarks>
    /// A longer device, such as a long User-Agent header of an unknown browser, is shortened to this length. The shortened device is what the session
    /// store receives, in the <see cref="SessionRecord.Device"/> of a <see cref="SignInInfo"/> or a session record, and what the device of the previous
    /// generation's grace period is compared with (see <see cref="PreviousGenerationGracePeriod"/>).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int MaxDeviceLength {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
            field = value;
        }
    } = DefaultMaxDeviceLength;

    /// <summary>
    /// Gets or sets the forced session access options that are applied to all session token retrievals. Default is <see cref="SessionAccessOptions.None"/>.
    /// </summary>
    public SessionAccessOptions ForcedAccessOptions {
        get;
        set {
            EnsureNotFrozen();
            value.ThrowIfFlagsAreNotDefined(nameof(value));
            field = value;
        }
    } = SessionAccessOptions.None;

    /// <summary>
    /// Gets or sets how long a session cookie is used before the session is validated against the session store and refreshed. Must be greater than zero.
    /// Default is 10 minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cookie whose validity period is shorter, which it is near the end of a maximum session lifetime, is validated when its validity period ends.
    /// </para>
    /// <para>
    /// A request notices changes in the session store, such as an ended session, when it validates the session, so up to this long after the change
    /// unless it forces validation (see <see cref="SessionAccessOptions.ForceValidate"/>).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan RefreshAfter {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or sets how long after a session is refreshed a session cookie of the previous generation is still accepted from the same device. Must be at
    /// least zero. Default is 2 minutes.
    /// </summary>
    /// <remarks>
    /// The grace period starts when the session store is updated with the new generation, as the response with the refreshed cookie starts. Concurrent
    /// requests, and connections that were established with the previous cookie, keep their session within it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan PreviousGenerationGracePeriod {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets how long after its last refresh a temporary (non-persistent) session expires. Must be longer than <see cref="RefreshAfter"/>. Default
    /// is 1 day.
    /// </summary>
    /// <remarks>
    /// That it is longer than <see cref="RefreshAfter"/> is checked when the application starts.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan TempSessionExpiry {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets how long after its last refresh a persistent session expires. Must be longer than <see cref="RefreshAfter"/>. Default is 30 days.
    /// </summary>
    /// <remarks>
    /// That it is longer than <see cref="RefreshAfter"/> is checked when the application starts.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan PersistentSessionExpiry {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets or sets the maximum amount of time after sign-in that a temporary (non-persistent) session remains valid, no matter how recently it was
    /// refreshed, or <see langword="null"/> for no limit. Must be greater than zero. Default is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limit is measured from the session's creation time (see <see cref="SessionRecord.CreatedUtc"/>). Like
    /// <see cref="TempSessionExpiry"/>, it is enforced when the session is validated against the session store, which happens when the session cookie is
    /// due for a refresh or the request forces validation.
    /// </para>
    /// <para>
    /// The validity period given to a session at sign-in and on each refresh never extends past the limit, so the session cookie is due for validation
    /// when the limit is reached.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan? MaxTempSessionLifetime {
        get;
        set {
            EnsureNotFrozen();
            if (value is { } lifetime)
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum amount of time after sign-in that a persistent session remains valid, no matter how recently it was refreshed, or
    /// <see langword="null"/> for no limit. Must be greater than zero. Default is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limit is measured from the session's creation time (see <see cref="SessionRecord.CreatedUtc"/>). Like
    /// <see cref="PersistentSessionExpiry"/>, it is enforced when the session is validated against the session store, which happens when the session
    /// cookie is due for a refresh or the request forces validation.
    /// </para>
    /// <para>
    /// The validity period given to a session at sign-in and on each refresh never extends past the limit, so the session cookie is due for validation
    /// when the limit is reached, and it expires then rather than a full expiry period later.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan? MaxPersistentSessionLifetime {
        get;
        set {
            EnsureNotFrozen();
            if (value is { } lifetime)
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the sessions of SignalR hub connections are revalidated, in an app that registers SignalR. Default is
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When a connection connects with a session cookie that is due for a refresh, its session is validated against the session store before the hub's
    /// <see cref="Microsoft.AspNetCore.SignalR.Hub.OnConnectedAsync"/> runs, as retrieving the session token would, without its origin and user ID
    /// precondition checks. A session that has ended leaves the connection without a session, and an <see cref="UnauthorizedApiException"/> from the store
    /// fails the connection. Exceptions from this validation are reported as <see cref="ApiExceptionHubFilter"/> reports exceptions, whether that filter is
    /// added or not.
    /// </para>
    /// <para>
    /// The session token of each connection that has a session when it connects is kept. Connections without a session are not tracked. About once a
    /// minute, the sessions of the connections that were last checked at least <see cref="RefreshAfter"/> ago are looked up in the session store. A
    /// connection whose session has ended, expired or reached its maximum lifetime is closed, with the other connections with its token: each is sent a
    /// message with the error that ended the session, an <see cref="UnauthorizedApiException"/> with the <see cref="ApiErrorCodes.SignInRequired"/>
    /// error code or the one that the store threw, which the toolkit's client handles by closing the connection, and is aborted if it is still open 5
    /// seconds later. Its hub method calls fail with the error meanwhile. A session that ends in any way, such as with a sign-out, a sign-in that replaces
    /// it, or the app removing it from the store, closes its connections at their next check on each server that has them, within about
    /// <see cref="RefreshAfter"/> and a minute of its end.
    /// </para>
    /// <para>
    /// An open connection counts as use of its session: a live session's expiry time is moved to a full expiry period after the check, capped at the end
    /// of its maximum lifetime, through <see cref="ISessionStore{TSessionToken}.TryRefreshSessionAsync"/>. Its refresh time and generation do not change,
    /// since the connection cannot deliver a new session cookie to the client, and the client's session cookie stays valid for its requests. If a request
    /// refreshes the session in the meantime, the session is looked up again.
    /// </para>
    /// </remarks>
    public bool RevalidateHubConnections {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    } = true;

    /// <summary>
    /// Gets a value indicating whether the options are in use: session handling checked them (see <see cref="Validate"/>) when it took them, and they can no
    /// longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as session handling does when it takes them. The session expiries are longer than <see cref="RefreshAfter"/>, and a
    /// <see cref="SessionCookieName"/> that starts with <c>__Host-</c> has no <see cref="CookieDomain"/> and the <see cref="CookiePath"/> <c>/</c>, as browsers
    /// require.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    internal TimeSpan GetSessionExpiry(bool persistent) => persistent ? PersistentSessionExpiry : TempSessionExpiry;

    internal TimeSpan? GetMaxSessionLifetime(bool persistent) => persistent ? MaxPersistentSessionLifetime : MaxTempSessionLifetime;

    /// <summary>
    /// Gets the time when a session reaches its maximum lifetime, or <see langword="null"/> if no maximum lifetime is set for its kind.
    /// </summary>
    internal DateTime? GetLifetimeEndUtc(bool persistent, DateTime createdUtc)
    {
        return GetMaxSessionLifetime(persistent) is { } maxLifetime ? SessionTime.Add(createdUtc, maxLifetime) : null;
    }

    /// <summary>
    /// Gets the validity period for a session that is refreshed (or created) at the specified time: the expiry for its kind, capped at the time left until
    /// its maximum lifetime. Returns <see langword="null"/> if the session has reached its maximum lifetime.
    /// </summary>
    internal TimeSpan? GetValidFor(bool persistent, DateTime createdUtc, DateTime utcNow)
    {
        var validFor = GetSessionExpiry(persistent);

        if (GetLifetimeEndUtc(persistent, createdUtc) is { } lifetimeEndUtc)
        {
            var remaining = lifetimeEndUtc - utcNow;

            if (remaining <= TimeSpan.Zero)
                return null;

            if (remaining < validFor)
                validFor = remaining;
        }

        return validFor;
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when session handling takes them. Options that are
    /// frozen already stay as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    internal void Freeze()
    {
        if (IsFrozen)
            return;

        Validate();
        IsFrozen = true;
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        const string HostCookiePrefix = "__Host-";
        List<string> problems = [];

        // A session is only refreshed when its cookie is due, after RefreshAfter, so an expiry that is not longer than that ends sessions that are in use.
        CheckExpiry(TempSessionExpiry, nameof(TempSessionExpiry));
        CheckExpiry(PersistentSessionExpiry, nameof(PersistentSessionExpiry));

        // Browsers reject a __Host- cookie that has a domain or a path other than "/", which would leave every client without a session.
        if (SessionCookieName.StartsWith(HostCookiePrefix, StringComparison.OrdinalIgnoreCase) && (CookieDomain is not null || CookiePath is not "/"))
        {
            problems.Add(
                $"{nameof(SessionHandlingOptions)}.{nameof(SessionCookieName)} '{SessionCookieName}' starts with '{HostCookiePrefix}', which browsers only " +
                $"accept for a cookie without a domain and with the path '/', but " +
                (CookieDomain is not null ? $"{nameof(CookieDomain)} is '{CookieDomain}'. " : $"{nameof(CookiePath)} is '{CookiePath}'. ") +
                "Choose a cookie name without the prefix (and the same name in the clients).");
        }

        return problems;

        void CheckExpiry(TimeSpan expiry, string propertyName)
        {
            if (expiry <= RefreshAfter)
            {
                problems.Add(
                    $"{nameof(SessionHandlingOptions)}.{propertyName} ({expiry}) must be longer than {nameof(SessionHandlingOptions)}." +
                    $"{nameof(RefreshAfter)} ({RefreshAfter}), otherwise sessions end before they are refreshed.");
            }
        }
    }

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(SessionHandlingOptions)} are in use, so they can no longer be changed.");
    }
}
