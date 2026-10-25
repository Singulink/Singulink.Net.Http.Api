using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Validates <see cref="CloudflareForwardingOptions"/> when the application starts.
/// </summary>
internal sealed class CloudflareForwardingOptionsValidator : IValidateOptions<CloudflareForwardingOptions>
{
    public ValidateOptionsResult Validate(string? name, CloudflareForwardingOptions options)
    {
        if (name != Options.DefaultName)
            return ValidateOptionsResult.Skip;

        foreach (string entry in options.TrustedProxyNetworks)
        {
            if (!CloudflareForwardingOptions.TryParseNetwork(entry, out _))
            {
                return ValidateOptionsResult.Fail(
                    $"'{entry}' in {nameof(CloudflareForwardingOptions)}.{nameof(CloudflareForwardingOptions.TrustedProxyNetworks)} is not a valid IP " +
                    "address or network in CIDR notation (for example 127.0.0.1 or 10.0.0.0/8).");
            }
        }

        if (options.SavedRangesPath is null)
        {
            string purpose = options.DownloadRanges
                ? "Cloudflare's IP ranges are saved to this file and read from it when they cannot be downloaded at startup. Use a location that survives " +
                    "restarts and redeployments, for example \"../data/cloudflare-ip-ranges.txt\"."
                : "Downloads are turned off, so Cloudflare's IP ranges are read from this file.";

            return ValidateOptionsResult.Fail(
                $"Cloudflare forwarding requires {nameof(CloudflareForwardingOptions)}.{nameof(CloudflareForwardingOptions.SavedRangesPath)} to be set. " +
                purpose);
        }

        return ValidateOptionsResult.Success;
    }
}
