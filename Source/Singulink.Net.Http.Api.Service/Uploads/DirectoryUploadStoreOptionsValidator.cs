using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Validates <see cref="DirectoryUploadStoreOptions"/> when the application starts (see <see cref="DirectoryUploadStoreOptions.Validate"/>). It checks
/// options of every name.
/// </summary>
internal sealed class DirectoryUploadStoreOptionsValidator : IValidateOptions<DirectoryUploadStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, DirectoryUploadStoreOptions options)
    {
        var problems = options.GetProblems();
        return problems.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
