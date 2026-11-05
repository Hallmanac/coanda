using System.Text;

namespace Coanda.Core.Projects;

/// <summary>
/// One of a project's repos as the core knows it: a git remote, and the name it goes by, which
/// <see cref="NameOf"/> derives from the remote. The core clones, fetches, and pushes refs to a repo and
/// knows nothing else of it: no branch, worktree, or pull request.
/// </summary>
public sealed record ProjectRepo(string Name, string Remote)
{
    /// <summary>The name a repo goes by when its remote leaves nothing that can stand as one.</summary>
    public const string Fallback = "repo";

    /// <summary>The longest name <see cref="NameOf"/> gives.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The names Windows reserves for devices, which no file or directory can take, with or without an extension.</summary>
    private static readonly HashSet<string> ReservedOnWindows = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>The repo at <paramref name="remote"/>, named by <see cref="NameOf"/>, with the remote recorded exactly as given.</summary>
    public static ProjectRepo FromRemote(string remote) => new(NameOf(remote), remote);

    /// <summary>
    /// The name a repo at <paramref name="remote"/> goes by. The remote's last path segment is taken,
    /// after any trailing slashes and one trailing <c>.git</c> are dropped, where a segment ends at the
    /// last <c>/</c>, <c>\</c>, or <c>:</c>, so a URL, an scp-like address, and a local path all name
    /// their repository. It is lowercased, every character other than a letter from a to z, a digit,
    /// <c>.</c>, <c>_</c>, and <c>-</c> becomes <c>-</c>, runs of <c>-</c> become one, leading and
    /// trailing <c>.</c> and <c>-</c> are dropped, and it is cut to <see cref="MaxNameLength"/>
    /// characters. What is left empty is <see cref="Fallback"/>, and a name Windows reserves for a
    /// device, alone or before a dot, gains <c>-repo</c> before the dot. The name is therefore always one plain
    /// directory name on macOS, Linux, and Windows, whatever the remote holds, and two remotes that
    /// differ only in letter case name the same repo.
    /// </summary>
    public static string NameOf(string remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        string trimmed = remote.Trim().TrimEnd('/', '\\');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].TrimEnd('/', '\\');
        }

        string segment = trimmed[(trimmed.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        var name = new StringBuilder(segment.Length);
        foreach (char c in segment.ToLowerInvariant())
        {
            char kept = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' ? c : '-';
            if (kept == '-' && name.Length > 0 && name[^1] == '-')
            {
                continue;
            }

            name.Append(kept);
        }

        string cut = name.ToString().Trim('.', '-');
        cut = cut.Length > MaxNameLength ? cut[..MaxNameLength].TrimEnd('.', '-') : cut;
        if (cut.Length == 0)
        {
            return Fallback;
        }

        int stem = cut.IndexOf('.', StringComparison.Ordinal) is var dot and >= 0 ? dot : cut.Length;
        return ReservedOnWindows.Contains(cut[..stem])
            ? cut.Insert(stem, $"-{Fallback}")
            : cut;
    }
}
