namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Parses the paths of tests, which are full paths, into Singulink.IO paths.
/// </summary>
internal static class TestPaths
{
    public static IAbsoluteDirectoryPath ToDirectory(string path) => DirectoryPath.ParseAbsolute(path, PathOptions.None);

    public static IAbsoluteFilePath ToFile(string path) => FilePath.ParseAbsolute(path, PathOptions.None);
}
