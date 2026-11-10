namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Provides the limits of the open uploads of users, in place of the limits of the options (see <see cref="ChunkedUploadOptions.MaxUploadsPerUser"/> and
/// <see cref="ChunkedUploadOptions.MaxTotalSizePerUser"/>). Chunked uploads use the provider that the app registers as an
/// <see cref="IUploadLimitsProvider"/> service, of any lifetime. Without a provider, the limits of the options apply.
/// </summary>
/// <remarks>
/// The limits apply to the creations of uploads, and to direct uploads: the length of a new upload against the user's total, the count of the user's
/// open uploads, and the total of their lengths. They apply as the options' limits do (see <see cref="ChunkedUploadOptions.MaxUploadsPerUser"/>).
/// <see cref="ChunkedUploadOptions.MaxTotalSize"/> applies to all users together as it is.
/// </remarks>
public interface IUploadLimitsProvider
{
    /// <summary>
    /// Gets the limits of the open uploads of a request's user, or <see langword="null"/> for the limits of the options.
    /// </summary>
    /// <param name="context">The context of the request, whose services the provider is resolved from.</param>
    /// <param name="userId">The ID of the request's user, who the upload is for.</param>
    /// <param name="cancellationToken">A token that is cancelled when the request is aborted.</param>
    /// <returns>The limits of the user, or <see langword="null"/> for the limits of the options.</returns>
    /// <remarks>
    /// It is called once for each creation and each direct upload whose ID, file name, length, content type and idempotency key are valid, before the limits
    /// are checked and anything is stored. An exception that it throws fails the request, as an exception of an endpoint does.
    /// </remarks>
    ValueTask<UploadLimits?> GetLimitsAsync(HttpContext context, string userId, CancellationToken cancellationToken);
}
