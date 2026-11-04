namespace Coanda.Daemon.Repositories;

/// <summary>One entry of <c>git worktree list --porcelain</c>.</summary>
public sealed record ListedWorktree(string Path, string? Head, string? Branch, bool Bare, bool Prunable, string? Locked)
{
    /// <summary>
    /// Whether the entry is left over rather than usable: git calls it prunable, which it does when
    /// its directory is missing, or its add never finished, which leaves it locked as <c>initializing</c>
    /// (and keeps git from calling it prunable even once its directory is gone).
    /// </summary>
    public bool Stale => Prunable || Locked == "initializing";

    /// <summary>Parses the porcelain listing: entries separated by a blank line, each a run of <c>key value</c> lines.</summary>
    public static IReadOnlyList<ListedWorktree> Parse(string porcelain)
    {
        List<ListedWorktree> entries = [];
        ListedWorktree? current = null;
        foreach (string line in porcelain.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                if (current is not null)
                {
                    entries.Add(current);
                }

                current = null;
                continue;
            }

            int space = line.IndexOf(' ');
            string key = space < 0 ? line : line[..space];
            string value = space < 0 ? string.Empty : line[(space + 1)..];
            current = key switch
            {
                "worktree" => new ListedWorktree(value, null, null, false, false, null),
                _ when current is null => null,
                "HEAD" => current with { Head = value },
                "branch" => current with { Branch = value },
                "bare" => current with { Bare = true },
                "prunable" => current with { Prunable = true },
                "locked" => current with { Locked = value },
                _ => current,
            };
        }

        if (current is not null)
        {
            entries.Add(current);
        }

        return entries;
    }
}
