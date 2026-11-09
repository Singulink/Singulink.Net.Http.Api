using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Validates <see cref="CloudflareForwardingOptions"/> when the application starts (see <see cref="CloudflareForwardingOptions.Validate"/>). It checks
/// options of every name.
/// </summary>
internal sealed class CloudflareForwardingOptionsValidator : IValidateOptions<CloudflareForwardingOptions>
{
    public ValidateOptionsResult Validate(string? name, CloudflareForwardingOptions options)
    {
        var problems = options.GetProblems();
        return problems.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
