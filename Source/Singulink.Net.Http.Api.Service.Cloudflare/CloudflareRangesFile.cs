using System.Net;
using System.Text;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Reads and writes the saved copy of Cloudflare's IP ranges, and parses range lists. The file has one range per line, like Cloudflare's lists, and must end
/// with <see cref="EndLine"/> so that a file read before it was completely written is never used.
/// </summary>
internal static class CloudflareRangesFile
{
    /// <summary>
    /// The line that the file must end with. Only blank lines may follow it.
    /// </summary>
    public const string EndLine = "# end";

    /// <summary>
    /// Reads the file. Throws <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/> if it does not exist, and <see
    /// cref="InvalidDataException"/> if it is not a complete list.
    /// </summary>
    public static async Task<(IPNetwork[] Networks, DateTimeOffset SavedAt)> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, useAsync: true);

        // Files created by hand have nothing but the ranges, so the date of the list is the time the file was last written. It is read from the open file
        // so that it always belongs to the contents that are read.
        var savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(stream.SafeFileHandle));

        using var reader = new StreamReader(stream);
        string contents = await reader.ReadToEndAsync(cancellationToken);

        return (Parse(contents), savedAt);
    }

    /// <summary>
    /// Replaces the file in a single step, so that readers see the previous file or the new one but never a partly written one, even after a power loss.
    /// </summary>
    public static async Task WriteAsync(string path, IPNetwork[] networks, CancellationToken cancellationToken)
    {
        // Unique, so that processes that save to the same file (such as servers that share it on a network) never write to the same temporary file.
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        var builder = new StringBuilder();

        foreach (var network in networks)
            builder.AppendLine(network.ToString());

        builder.AppendLine(EndLine);
        byte[] contents = Encoding.UTF8.GetBytes(builder.ToString());

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, useAsync: true))
            {
                await stream.WriteAsync(contents, cancellationToken);

                // Without this, the rename can reach the disk before the data does, and a power loss would leave an empty or partial file.
                stream.Flush(flushToDisk: true);
            }

            await ReplaceAsync(tempPath, path, cancellationToken);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        // On Windows, a file cannot be replaced while another process reads it (such as another server that shares it), but reads only take a moment.
        static async Task ReplaceAsync(string tempPath, string path, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Parses a list of IP ranges with one range per line, skipping blank lines and comments. Fails if any other line is not a valid range, so that an
    /// unexpected response (such as an error page) is never partially trusted.
    /// </summary>
    public static bool TryParseList(ReadOnlySpan<char> list, List<IPNetwork> networks, out int count)
    {
        int initialCount = networks.Count;
        count = 0;

        foreach (var rawLine in list.EnumerateLines())
        {
            var line = rawLine.Trim();

            if (line.IsEmpty || line[0] is '#')
                continue;

            if (!IPNetwork.TryParse(line, out var network))
                return false;

            networks.Add(network);
        }

        count = networks.Count - initialCount;
        return true;
    }

    private static IPNetwork[] Parse(string contents)
    {
        var trimmed = contents.AsSpan().TrimEnd();
        int lastLineStart = trimmed.LastIndexOfAny('\r', '\n') + 1;

        // Whatever writes the file writes it from the start, so a file that is read before it is complete always lacks the last line.
        if (!trimmed[lastLineStart..].Trim().SequenceEqual(EndLine))
            throw new InvalidDataException($"The file does not end with a \"{EndLine}\" line, so it may be incomplete.");

        var networks = new List<IPNetwork>();

        if (!TryParseList(trimmed[..lastLineStart], networks, out int count) || count is 0)
            throw new InvalidDataException("The file is not a list of IP ranges.");

        return [.. networks];
    }
}
