using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Denies the current user the listing of a directory, the creation of files in it, or the reading of a file, until it is disposed, as Windows does for a
/// moment to a directory that is being removed, or to a file that is being deleted. On Windows, it can also deny the reading of the files that are created
/// in a directory, as another process that opens a new file without sharing it does.
/// </summary>
public sealed class AccessDenial : IDisposable
{
    private readonly string _path;
    private readonly Access _access;
    private readonly UnixFileMode _mode;

    private AccessDenial(string path, Access access)
    {
        _path = path;
        _access = access;

        if (OperatingSystem.IsWindows())
        {
            ChangeRule(add: true);
        }
        else
        {
            _mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, _mode & ~(access is Access.CreatingFiles ? UnixFileMode.UserWrite : UnixFileMode.UserRead));
        }
    }

    private enum Access
    {
        Listing,
        CreatingFiles,
        Reading,
        ReadingNewFiles,
    }

    public static AccessDenial DenyListing(string directory) => new(directory, Access.Listing);

    public static AccessDenial DenyCreatingFiles(string directory) => new(directory, Access.CreatingFiles);

    public static AccessDenial DenyReading(string file) => new(file, Access.Reading);

    [SupportedOSPlatform("windows")]
    public static AccessDenial DenyReadingNewFiles(string directory) => new(directory, Access.ReadingNewFiles);

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            ChangeRule(add: false);
        else
            File.SetUnixFileMode(_path, _mode);
    }

    [SupportedOSPlatform("windows")]
    private static void ChangeRule(FileSystemSecurity security, FileSystemAccessRule rule, bool add)
    {
        if (add)
            security.AddAccessRule(rule);
        else
            security.RemoveAccessRule(rule);
    }

    [SupportedOSPlatform("windows")]
    private void ChangeRule(bool add)
    {
        var rights = _access switch {
            Access.Listing => FileSystemRights.ListDirectory,
            Access.CreatingFiles => FileSystemRights.CreateFiles,
            _ => FileSystemRights.ReadData,
        };

        var rule = _access is Access.ReadingNewFiles
            ? new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, rights, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Deny)
            : new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);

        if (_access is Access.Reading)
        {
            var file = new FileInfo(_path);
            var security = file.GetAccessControl();
            ChangeRule(security, rule, add);
            file.SetAccessControl(security);
        }
        else
        {
            var directory = new DirectoryInfo(_path);
            var security = directory.GetAccessControl();
            ChangeRule(security, rule, add);
            directory.SetAccessControl(security);
        }
    }
}
