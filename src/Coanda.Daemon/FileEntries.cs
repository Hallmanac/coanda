namespace Coanda.Daemon;

/// <summary>How the daemon walks a folder whose entries it must not follow: every entry, and links named as such.</summary>
public static class FileEntries
{
    /// <summary>Every entry of one folder, hidden and system ones included, with an error for one that cannot be read.</summary>
    public static EnumerationOptions Everything { get; } = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        MatchType = MatchType.Simple,
        RecurseSubdirectories = false,
    };

    /// <summary>Whether <paramref name="entry"/> is a symbolic link or another reparse point, such as a Windows junction, whether or not its target exists.</summary>
    public static bool IsLink(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.LinkTarget is not null
            || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>
    /// Removes whatever is at <paramref name="path"/>, never following a link: a link is removed
    /// itself, a file is deleted, and a folder has each of its entries removed the same way before it
    /// is deleted. Nothing at the path is not an error.
    /// </summary>
    public static void Remove(string path)
    {
        var entry = new FileInfo(path);
        if (IsLink(entry))
        {
            if (OperatingSystem.IsWindows() && (int)entry.Attributes != -1 && entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if (File.Exists(path))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            File.Delete(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (FileSystemInfo inner in new DirectoryInfo(path).EnumerateFileSystemInfos("*", Everything))
        {
            Remove(inner.FullName);
        }

        Directory.Delete(path);
    }
}
