using System.Text;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// The file the review diff step writes for the reviewers: <c>review.diff</c> under <c>.coanda/</c> at
/// the root of the flow run's work path, the directory where the daemon writes an agent session's own
/// files. Whatever is at either path is replaced rather than written or deleted through, as the
/// daemon treats its own files there: a file or a link at <c>.coanda</c> is removed, and so is one at
/// <c>.coanda/review.diff</c>, so a session that turned either into a link to somewhere else never has
/// the module write or delete there. The clone's <c>info/exclude</c> hides <c>.coanda/</c> from git, so
/// the file is never committed.
/// </summary>
public static class ReviewDiffFile
{
    /// <summary>The directory, relative to the work path, that the daemon's session files live in too.</summary>
    public const string DirectoryName = ".coanda";

    /// <summary>The file's name in <see cref="DirectoryName"/>.</summary>
    public const string FileName = "review.diff";

    /// <summary>The file, relative to the work path, as the reviewers are told to read it; every platform reads the forward slash.</summary>
    public const string RelativePath = DirectoryName + "/" + FileName;

    /// <summary>The mode the file is created with on macOS and Linux: 0600, as the daemon's own files there have.</summary>
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Writes <paramref name="text"/> as the file in <paramref name="workPath"/>, replacing whatever is there.</summary>
    public static void Write(string workPath, string text)
    {
        string directory = ReplaceLinkedDirectory(workPath);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        File.Delete(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    /// <summary>Removes the file from <paramref name="workPath"/>, if anything is there, without deleting through a link.</summary>
    public static void Remove(string workPath)
    {
        string directory = ReplaceLinkedDirectory(workPath);
        if (Directory.Exists(directory))
        {
            File.Delete(Path.Combine(directory, FileName));
        }
    }

    /// <summary>
    /// The path of <see cref="DirectoryName"/> in <paramref name="workPath"/>, once a file or a link
    /// there, which would have the module write through it, has been removed.
    /// </summary>
    private static string ReplaceLinkedDirectory(string workPath)
    {
        string directory = Path.Combine(workPath, DirectoryName);
        if (File.Exists(directory) || Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is not null)
        {
            File.Delete(directory);
        }

        return directory;
    }
}
