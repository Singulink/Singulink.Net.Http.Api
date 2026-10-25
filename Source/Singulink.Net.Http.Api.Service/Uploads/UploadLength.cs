namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// The length of an open upload in its server's total (see <see cref="UploadTotal"/>), and the time it expires if it has received no chunk by then.
/// </summary>
/// <param name="Length">The length of the upload.</param>
/// <param name="UnstartedExpiresUtc">The time the upload expires unless a chunk arrives before it, or <see langword="null"/> if one has.</param>
internal readonly record struct UploadLength(long Length, DateTime? UnstartedExpiresUtc);
