using System.Text.RegularExpressions;
using Coanda.Sdk.Commands;

namespace Coanda.Modules.Software.GitHub;

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
    /// The remote as a message may quote it, with any credential written into it removed. The user
    /// information is what precedes the last <c>@</c> before the first <c>/</c> after the scheme (or
    /// the start, for the <c>user@host:path</c> form), so an <c>@</c> in the path is kept. A remote
    /// with a scheme whose host part holds no <c>@</c> but does hold a <c>:</c> that starts no port
    /// reads as a password with a <c>/</c> in it, so there everything up to the last <c>@</c> goes.
    /// </summary>
    public static string Redact(string remote)
    {
        int scheme = remote.IndexOf("://", StringComparison.Ordinal);
        int start = scheme < 0
            ? 0
            : scheme + 3;
        int slash = remote.IndexOf('/', start);
        string authority = slash < 0
            ? remote[start..]
            : remote[start..slash];
        int at = authority.LastIndexOf('@') switch
        {
            >= 0 and int inAuthority => start + inAuthority,
            _ when scheme >= 0 && HasPassword(authority) => remote.LastIndexOf('@'),
            _ => -1,
        };
        string kept = at < start
            ? remote
            : remote[..start] + remote[(at + 1)..];
        return OutputText.Redact(kept);
    }

    /// <summary>Whether a host part holds a <c>:</c> followed by anything but a port's digits.</summary>
    private static bool HasPassword(string authority)
    {
        int colon = authority.IndexOf(':');
        return colon >= 0 && !(authority.Length > colon + 1 && authority[(colon + 1)..].All(char.IsAsciiDigit));
    }

    public override string ToString() => $"{Owner}/{Name}";

    // The name is matched lazily, so a trailing .git is left to the optional group rather than kept in the name.
    [GeneratedRegex(@"\A(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)(?<owner>[A-Za-z0-9_.\-]+)/(?<name>[A-Za-z0-9_.\-]+?)(?:\.git)?\z")]
    private static partial Regex Remote();
}
