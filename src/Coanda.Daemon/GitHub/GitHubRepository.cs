using System.Text.RegularExpressions;
using Coanda.Daemon.Commands;

namespace Coanda.Daemon.GitHub;

/// <summary>
/// A github.com repository, named by its owner and its name, as read from a project's remote. Both
/// are single path segments of letters, digits, hyphens, underscores, and periods, and neither is
/// <c>.</c> or <c>..</c>, so a request path built from them names that repository and nothing else.
/// </summary>
public sealed partial record GitHubRepository
{
    private GitHubRepository(string owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    public string Owner { get; }

    public string Name { get; }

    /// <summary>The remote forms <see cref="Parse"/> reads, for a message that names them.</summary>
    public const string Forms =
        "https://github.com/<owner>/<repo>, git@github.com:<owner>/<repo>, or ssh://git@github.com/<owner>/<repo>, each with or without a trailing .git";

    /// <summary>
    /// The repository <paramref name="remote"/> names, when it is one of the forms in <see cref="Forms"/>
    /// exactly; null for anything else, a remote carrying other user information or another host included.
    /// </summary>
    public static GitHubRepository? Parse(string remote)
    {
        Match match = Remote().Match(remote);
        if (!match.Success)
        {
            return null;
        }

        string owner = match.Groups["owner"].Value;
        string name = match.Groups["name"].Value;
        // A last segment of .git alone names no repository: taking its .git off would leave nothing.
        return owner is "." or ".." || name is "." or ".." or ".git"
            ? null
            : new GitHubRepository(owner, name);
    }

    /// <summary>
    /// The remote as a message may quote it: with whatever precedes the last <c>@</c> before its path
    /// removed, after the scheme when it has one, so a credential written into it is never quoted.
    /// </summary>
    public static string Redact(string remote)
    {
        int scheme = remote.IndexOf("://", StringComparison.Ordinal);
        int start = scheme < 0 ? 0 : scheme + 3;
        int at = remote.LastIndexOf('@');
        string kept = at < start ? remote : remote[..start] + remote[(at + 1)..];
        return OutputText.Redact(kept);
    }

    public override string ToString() => $"{Owner}/{Name}";

    // The name is matched lazily, so a trailing .git is left to the optional group rather than kept in the name.
    [GeneratedRegex(@"\A(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)(?<owner>[A-Za-z0-9_.\-]+)/(?<name>[A-Za-z0-9_.\-]+?)(?:\.git)?\z")]
    private static partial Regex Remote();
}
