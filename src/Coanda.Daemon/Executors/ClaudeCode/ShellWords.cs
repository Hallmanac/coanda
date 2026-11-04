using System.Text;
using System.Text.RegularExpressions;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// The words of a shell command as the tool guard reads them, and how a word or a pattern becomes a
/// path. This is a best-effort reading of the command's text, not a shell: it splits the text into
/// commands at line ends and at the shell's command operators, and each command into words on
/// whitespace and redirections, whatever quotes surround them, and then drops the quotes, so an
/// unbalanced quote (an apostrophe in a heredoc, say) changes nothing.
/// </summary>
public static partial class ShellWords
{
    /// <summary>The characters that end one command and begin the next, besides a line end, including those that open and close a subshell.</summary>
    private const string CommandOperators = "|&;()`";

    /// <summary>The characters that end a word within one command, besides whitespace.</summary>
    private const string Redirections = "<>";

    /// <summary>The characters that make a pattern or a word a wildcard.</summary>
    private const string Wildcards = "*?[{";

    /// <summary>
    /// The commands of <paramref name="command"/>, in order, each as its words with their quotes
    /// removed and with how many subshells (parentheses, <c>$(</c>, or backticks) it runs inside.
    /// </summary>
    public static IReadOnlyList<ShellCommand> Commands(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        List<ShellCommand> commands = [];
        List<string> words = [];
        var word = new StringBuilder();
        int depth = 0;
        bool backticks = false;
        foreach (char c in command)
        {
            if (c is '\n' or '\r' || CommandOperators.Contains(c, StringComparison.Ordinal))
            {
                EndWord();
                EndCommand();
                switch (c)
                {
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth = Math.Max(0, depth - 1);
                        break;
                    case '`':
                        backticks = !backticks;
                        break;
                }
            }
            else if (char.IsWhiteSpace(c) || Redirections.Contains(c, StringComparison.Ordinal))
            {
                EndWord();
            }
            else if (c is not ('\'' or '"'))
            {
                word.Append(c);
            }
        }

        EndWord();
        EndCommand();
        return commands;

        void EndWord()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        void EndCommand()
        {
            if (words.Count > 0)
            {
                commands.Add(new ShellCommand([.. words], backticks ? depth + 1 : depth));
                words.Clear();
            }
        }
    }

    /// <summary>
    /// Where a command changes the shell's directory to, when its first word after any leading
    /// <c>NAME=value</c> assignments is <c>cd</c> or <c>pushd</c>: the first argument that is not an
    /// option, which is <c>-</c> for <c>cd -</c>; <c>~</c> for a <c>cd</c> with none; and null for a
    /// <c>pushd</c> with none, which only reorders directories the shell has already been in, or for
    /// any other command.
    /// </summary>
    public static string? DirectoryChange(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        int first = 0;
        while (first < words.Count && Assignment().IsMatch(words[first]))
        {
            first++;
        }

        string? name = first < words.Count
            ? words[first]
            : null;
        if (name is not ("cd" or "pushd"))
        {
            return null;
        }

        // An option begins with '-' (and '+' rotates pushd's stack); a lone '-' is cd's previous directory.
        string? destination = words.Skip(first + 1).FirstOrDefault(w => w == "-" || (w[0] is not ('-' or '+')));
        return destination ?? (name == "cd" ? "~" : null);
    }

    /// <summary>
    /// <paramref name="text"/> with a leading <c>~</c> replaced by the home directory, and every
    /// <c>$NAME</c> and <c>${NAME}</c> replaced by what <paramref name="value"/> gives for the name,
    /// which is the empty string for a variable the shell does not define, as the shell expands it.
    /// </summary>
    public static string Expand(string text, Func<string, string> value, string home)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(value);
        string expanded = (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || (OperatingSystem.IsWindows() && text.StartsWith("~\\", StringComparison.Ordinal)))
            ? home + text[1..]
            : text;
        return Variable().Replace(expanded, match => value(match.Groups["braced"].Success ? match.Groups["braced"].Value : match.Groups["bare"].Value));
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

        int separator = text.LastIndexOfAny(Separators, wildcard);
        return separator < 0
            ? string.Empty
            : text[..(separator + 1)];
    }

    /// <summary>
    /// Whether the part of a pattern or word from its first wildcard component on may climb to a
    /// parent directory, which <see cref="BeforeWildcard"/> leaves unjudged: a component that is
    /// <c>..</c>, alone or as one choice of a brace, or that begins with <c>.</c> followed by a
    /// wildcard or a second <c>.</c>, which a shell may match to <c>..</c>.
    /// </summary>
    public static bool MayClimbAfterWildcard(string text)
    {
        string before = BeforeWildcard(text);
        if (before.Length == text.Length)
        {
            return false;
        }

        foreach (string component in text[before.Length..].Split(Separators))
        {
            if (component.Split('{', '}', ',').Contains(".."))
            {
                return true;
            }

            if (component.Length > 1 && component[0] == '.' && (component[1] == '.' || Wildcards.Contains(component[1], StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static char[] Separators { get; } = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];

    [GeneratedRegex(@"\$(?:\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|(?<bare>[A-Za-z_][A-Za-z0-9_]*))")]
    private static partial Regex Variable();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();
}

/// <summary>One command of a shell command line: its words, and how many subshells it runs inside.</summary>
public sealed record ShellCommand(IReadOnlyList<string> Words, int Depth);
