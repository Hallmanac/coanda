using System.Text;
using System.Text.RegularExpressions;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// The words of a shell command as the tool guard reads them, and how a word or a pattern becomes a
/// path. This is a best-effort reading of the command's text, not a shell: it splits the text on
/// whitespace and on the shell's operator characters, whatever quotes surround them, and then drops
/// the quotes, so an unbalanced quote (an apostrophe in a heredoc, say) changes nothing.
/// </summary>
public static partial class ShellWords
{
    /// <summary>The characters a command is split on besides whitespace.</summary>
    private const string Operators = "|&;<>()`";

    /// <summary>The characters that make a pattern or a word a wildcard.</summary>
    private const string Wildcards = "*?[{";

    /// <summary>The words of <paramref name="command"/>, each with its quotes removed, in order.</summary>
    public static IReadOnlyList<string> Split(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        List<string> words = [];
        var word = new StringBuilder();
        foreach (char c in command)
        {
            if (char.IsWhiteSpace(c) || Operators.Contains(c, StringComparison.Ordinal))
            {
                Flush();
            }
            else if (c is not ('\'' or '"'))
            {
                word.Append(c);
            }
        }

        Flush();
        return words;

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
    }

    /// <summary>
    /// <paramref name="text"/> with a leading <c>~</c> replaced by the home directory, and every
    /// <c>$NAME</c> and <c>${NAME}</c> that <paramref name="environment"/> defines replaced by its
    /// value. A variable the environment does not define is left as written.
    /// </summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string> environment, string home)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(environment);
        string expanded = (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || (OperatingSystem.IsWindows() && text.StartsWith("~\\", StringComparison.Ordinal)))
            ? home + text[1..]
            : text;
        return Variable().Replace(expanded, match =>
        {
            string name = match.Groups["braced"].Success ? match.Groups["braced"].Value : match.Groups["bare"].Value;
            return environment.TryGetValue(name, out string? value) ? value : match.Value;
        });
    }

    /// <summary>
    /// Whether an expanded word names a path the guard judges: one that is absolute, begins with
    /// <c>~</c> or <c>.</c>, or holds a path separator.
    /// </summary>
    public static bool IsPathLike(string word) =>
        word.Length > 0
        && (Path.IsPathRooted(word)
            || word[0] is '~' or '.'
            || word.Contains('/', StringComparison.Ordinal)
            || (OperatingSystem.IsWindows() && word.Contains('\\', StringComparison.Ordinal)));

    /// <summary>
    /// The part of a pattern or word before the first path component that holds a wildcard, or the
    /// whole of it when it holds none. A first component holding one leaves nothing, which names
    /// the directory the pattern is resolved against.
    /// </summary>
    public static string BeforeWildcard(string text)
    {
        int wildcard = text.IndexOfAny(Wildcards.ToCharArray());
        if (wildcard < 0)
        {
            return text;
        }

        char[] separators = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];
        int separator = text.LastIndexOfAny(separators, wildcard);
        return separator < 0
            ? string.Empty
            : text[..(separator + 1)];
    }

    [GeneratedRegex(@"\$(?:\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|(?<bare>[A-Za-z_][A-Za-z0-9_]*))")]
    private static partial Regex Variable();
}
