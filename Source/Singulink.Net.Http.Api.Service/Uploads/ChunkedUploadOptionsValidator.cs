using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Validates <see cref="ChunkedUploadOptions"/> when the application starts.
/// </summary>
internal sealed class ChunkedUploadOptionsValidator : IValidateOptions<ChunkedUploadOptions>
{
    public ValidateOptionsResult Validate(string? name, ChunkedUploadOptions options)
    {
        if (name != Options.DefaultName)
            return ValidateOptionsResult.Skip;

        List<string> failures = [];

        if (options.MinChunkSize > options.MaxChunkSize)
        {
            failures.Add(
                $"{nameof(ChunkedUploadOptions)}.{nameof(ChunkedUploadOptions.MinChunkSize)} ({options.MinChunkSize}) is greater than " +
                $"{nameof(ChunkedUploadOptions.MaxChunkSize)} ({options.MaxChunkSize}).");
        }
        else if (options.DefaultChunkSize < options.MinChunkSize || options.DefaultChunkSize > options.MaxChunkSize)
        {
            failures.Add(
                $"{nameof(ChunkedUploadOptions)}.{nameof(ChunkedUploadOptions.DefaultChunkSize)} ({options.DefaultChunkSize}) is not from " +
                $"{nameof(ChunkedUploadOptions.MinChunkSize)} ({options.MinChunkSize}) to {nameof(ChunkedUploadOptions.MaxChunkSize)} " +
                $"({options.MaxChunkSize}).");
        }

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
