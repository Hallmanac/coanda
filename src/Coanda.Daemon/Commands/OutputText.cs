using System.Text.RegularExpressions;

namespace Coanda.Daemon.Commands;

/// <summary>
/// What is done to a command's output before any of it is stored on a flow run: cut to its last lines,
/// and stripped of the user information of every URL that has a scheme.
/// </summary>
public static partial class OutputText
{
    /// <summary>
    /// Removes whatever precedes the <c>@</c> before the host in every URL with a scheme, as in
    /// <c>https://user:secret@host/path</c>, which becomes <c>https://host/path</c>. A remote of the
    /// form <c>git@host:path</c> has no scheme and is left as written.
    /// </summary>
    public static string Redact(string text) => UserInformation().Replace(text, "${scheme}");

    /// <summary>The last <paramref name="count"/> lines of <paramref name="text"/>, with a trailing line break dropped and line endings made <c>\n</c>.</summary>
    public static IReadOnlyList<string> LastLines(string text, int count)
    {
        string[] lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        return lines is [""] ? [] : lines[Math.Max(0, lines.Length - count)..];
    }

    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.\-]*://)[^/\s@]*(?:@[^/\s@]*)*@")]
    private static partial Regex UserInformation();
}
