using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Validates <see cref="ChunkedUploadOptions"/> when the application starts (see <see cref="ChunkedUploadOptions.Validate"/>). It checks options of every
/// name.
/// </summary>
internal sealed class ChunkedUploadOptionsValidator : IValidateOptions<ChunkedUploadOptions>
{
    public ValidateOptionsResult Validate(string? name, ChunkedUploadOptions options)
    {
        var problems = options.GetProblems();
        return problems.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
