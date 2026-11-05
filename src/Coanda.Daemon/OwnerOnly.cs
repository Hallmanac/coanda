using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Coanda.Daemon;

/// <summary>
/// Access that only the current user holds: mode 0600 on macOS and Linux, and on Windows an access
/// list whose only entry grants the current user full control. On Windows the node home's own list
/// is not inherited from its parent, and the files created inside it inherit theirs from it. The
/// node home's files are created this way and narrowed to it when an earlier run left them wider.
/// </summary>
public static class OwnerOnly
{
    public const UnixFileMode UnixMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The mode an owner-only directory has on macOS and Linux: 0700.</summary>
    public const UnixFileMode UnixDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [SupportedOSPlatform("windows")]
    private static InheritanceFlags Inherited => InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Creates the directory if it is absent. On Windows it carries an owner-only access list that
    /// everything created inside it inherits, from its creation when this call creates it, and is
    /// narrowed to that when it already exists with any other. On macOS and Linux its mode is left as it is.
    /// </summary>
    public static void EnsureDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            if (directory.Parent is { } parent)
            {
                Directory.CreateDirectory(parent.FullName);
            }

            DirectorySecurity().CreateDirectory(directory.FullName);
            directory.Refresh();
        }

        if (!IsOwnerOnly(directory.GetAccessControl()))
        {
            directory.SetAccessControl(DirectorySecurity());
        }
    }

    /// <summary>
    /// Creates the directory owner-only if it is absent, and narrows it to owner-only if it exists:
    /// mode 0700 on macOS and Linux, and on Windows the owner-only access list <see cref="EnsureDirectory"/>
    /// gives. Unlike <see cref="EnsureDirectory"/>, it narrows an existing directory's mode on macOS and Linux too.
    /// </summary>
    public static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UnixDirectoryMode);
        File.SetUnixFileMode(path, UnixDirectoryMode);
    }

    /// <summary>
    /// Creates the directory owner-only if it is absent, and narrows it and every directory and file
    /// under it to owner-only, at any depth: on macOS and Linux mode 0700 for a directory and 0600 for a
    /// file, and on Windows an access list that is not inherited and whose only entry grants the current
    /// user full control. A symbolic link is left as it is and never followed, so nothing outside the
    /// directory is narrowed through one.
    /// </summary>
    public static void NarrowTree(string path)
    {
        EnsurePrivateDirectory(path);
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(path)]);
        while (pending.TryPop(out DirectoryInfo? directory))
        {
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if (entry is DirectoryInfo inner)
                {
                    EnsurePrivateDirectory(inner.FullName);
                    pending.Push(inner);
                }
                else
                {
                    Narrow(entry.FullName);
                }
            }
        }
    }

    /// <summary>Narrows a file that exists to owner-only access; a file that does not exist is left absent.</summary>
    public static void Narrow(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            new FileInfo(path).SetAccessControl(FileSecurity());
        }
        else
        {
            File.SetUnixFileMode(path, UnixMode);
        }
    }

    /// <summary>
    /// Opens a file that only its owner may read or write. On macOS and Linux a file this call
    /// creates is created with mode 0600 and an existing one is narrowed to it through the handle
    /// before anything is written, so the umask never widens it. On Windows the file takes the
    /// owner-only access list of the directory it is created in, which <see cref="EnsureDirectory"/> gives it.
    /// </summary>
    public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, options);
        }

        options.UnixCreateMode = UnixMode;
        var stream = new FileStream(path, options);
        try
        {
            File.SetUnixFileMode(stream.SafeFileHandle, UnixMode);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser() =>
        WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current Windows user has no security identifier.");

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity DirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser(), FileSystemRights.FullControl, Inherited, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity FileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Whether a directory's access list is protected from its parent and holds exactly one entry,
    /// granting the current user full control and inherited by everything inside it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsOwnerOnly(DirectorySecurity security)
    {
        AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        return security.AreAccessRulesProtected
            && rules.Count == 1
            && rules[0] is FileSystemAccessRule rule
            && rule.IdentityReference.Equals(CurrentUser())
            && rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights == FileSystemRights.FullControl
            && rule.InheritanceFlags == Inherited
            && rule.PropagationFlags == PropagationFlags.None;
    }
}
