using System.Collections.Frozen;
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
    /// removed, with how many subshells (parentheses, <c>$(</c>, or backticks) it runs inside, and
    /// with the operators on either side of it.
    /// </summary>
    public static IReadOnlyList<ShellCommand> Commands(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        List<ShellCommand> commands = [];
        List<string> words = [];
        var word = new StringBuilder();
        int depth = 0;
        bool backticks = false;
        string before = string.Empty;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '\\' && LineEndAt(command, i + 1) is int continuation and > 0)
            {
                // A backslash before a line end joins the two lines into one, as a shell does.
                i += continuation;
            }
            else if (c is '\n' or '\r' || CommandOperators.Contains(c, StringComparison.Ordinal))
            {
                EndWord();
                string op = c switch
                {
                    '&' or '|' when i + 1 < command.Length && command[i + 1] == c => new string(c, 2),
                    '\n' or '\r' => ";",
                    '`' => backticks ? ")" : "(",
                    _ => c.ToString(),
                };
                i += op.Length - 1;
                EndCommand(op);
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
        EndCommand(string.Empty);
        return commands;

        static int LineEndAt(string text, int at) => text.AsSpan(at) switch
        {
            ['\r', '\n', ..] => 2,
            ['\n', ..] or ['\r', ..] => 1,
            _ => 0,
        };

        void EndWord()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        void EndCommand(string after)
        {
            if (words.Count > 0)
            {
                commands.Add(new ShellCommand([.. words], backticks ? depth + 1 : depth, before, after));
                words.Clear();
            }

            before = after;
        }
    }

    /// <summary>
    /// How a command changes the shell's directory, when its first word after any leading
    /// <c>NAME=value</c> assignments, shell keywords (<c>then</c>, <c>do</c>, <c>{</c>, <c>!</c>, and
    /// the like), and the wrappers <c>builtin</c>, <c>command</c>, <c>eval</c>, <c>time</c>, <c>noglob</c>, and
    /// <c>nocorrect</c> is <c>cd</c>, <c>pushd</c>, or <c>popd</c>, read with its backslashes removed:
    /// to the first argument that is not an option (<see cref="DirectoryMove.To"/>), which is
    /// <c>-</c> for <c>cd -</c> and <c>~</c> for a <c>cd</c> with none; back to a directory the shell has
    /// been in (<see cref="DirectoryMove.Back"/>) for a <c>popd</c>, a <c>pushd</c> with no directory,
    /// and a <c>cd</c> or <c>pushd</c> to a place on the directory stack such as <c>+1</c>; and to a
    /// directory the guard cannot know (<see cref="DirectoryMove.Unknown"/>) for zsh's
    /// <c>cd OLD NEW</c>, which replaces part of the current directory's name. Null for any other
    /// command.
    /// </summary>
    public static DirectoryMove? DirectoryChange(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        int first = 0;
        while (first < words.Count && (Assignment().IsMatch(words[first]) || Prefixes.Contains(Unescape(words[first]))))
        {
            // command -p and time -p take an option before the command they run; command -v only prints it.
            first += Unescape(words[first]) is "command" or "time" && first + 1 < words.Count && words[first + 1] == "-p"
                ? 2
                : 1;
        }

        string? name = first < words.Count
            ? Unescape(words[first])
            : null;
        if (name is not ("cd" or "pushd" or "popd"))
        {
            return null;
        }

        // An option begins with '-' (and '+' rotates pushd's stack); a lone '-' is cd's previous directory.
        string[] arguments = [.. words.Skip(first + 1).SkipWhile(w => w != "-" && !StackPlace().IsMatch(w) && w[0] is '-' or '+')];
        return (name, arguments) switch
        {
            ("popd", _) or (not "cd", []) => DirectoryMove.Back,
            (_, [{ } place, ..]) when StackPlace().IsMatch(place) => DirectoryMove.Back,
            ("cd", []) => DirectoryMove.To("~"),
            ("cd", [_, _, ..]) => DirectoryMove.Unknown,
            (_, [{ } destination, ..]) => DirectoryMove.To(destination),
        };
    }

    /// <summary>
    /// <paramref name="text"/> with each backslash removed and the character after it kept, as a shell
    /// reads a word outside quotes; a backslash at its end is dropped.
    /// </summary>
    public static string Unescape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var unescaped = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\')
            {
                unescaped.Append(text[i]);
            }
            else if (i + 1 < text.Length)
            {
                unescaped.Append(text[++i]);
            }
        }

        return unescaped.ToString();
    }

    /// <summary>The words that may come before a command's name without being the command it runs.</summary>
    private static FrozenSet<string> Prefixes { get; } = FrozenSet.Create(
        StringComparer.Ordinal,
        "!", "{", "if", "then", "elif", "else", "while", "until", "do", "builtin", "command", "eval", "time", "noglob", "nocorrect");

    /// <summary>The most words a brace expansion may give before the guard stops expanding it.</summary>
    public const int BraceLimit = 256;

    /// <summary>
    /// The words a shell's brace expansion makes of <paramref name="text"/>, followed by the text
    /// itself, which is what a word whose braces were quoted names; or null when the expansion would
    /// give more than <see cref="BraceLimit"/> words. A brace expands when its matching closing brace
    /// follows with a comma between them at its own level, each choice in turn; a brace with none,
    /// such as a sequence like <c>{1..3}</c> or a variable's <c>${NAME}</c>, is kept as it is. A shell
    /// does not expand the brace of <c>${</c>, but expanding it here only adds words to judge. A choice may hold path separators or be empty, so the words may differ from one another
    /// in how many components they have, and a word may begin with <c>/</c> or <c>~</c>.
    /// </summary>
    public static IReadOnlyList<string>? Braces(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> words = [];
        if (!Expand(text, 0, words))
        {
            return null;
        }

        words.Add(text);
        return [.. words.Distinct(StringComparer.Ordinal)];

        bool Expand(string word, int from, List<string> into)
        {
            for (int open = word.IndexOf('{', from); open >= 0; open = word.IndexOf('{', open + 1))
            {
                if (Choices(word, open) is not { } choices)
                {
                    continue;
                }

                string prefix = word[..open];
                string suffix = word[(choices.Close + 1)..];
                foreach (string choice in choices.Texts)
                {
                    // What precedes the brace holds no brace that expands, so the search resumes after it.
                    if (!Expand(prefix + choice + suffix, open, into))
                    {
                        return false;
                    }
                }

                return true;
            }

            into.Add(word);
            return into.Count <= BraceLimit;
        }
    }

    /// <summary>
    /// The choices of the brace opening at <paramref name="open"/> and where it closes, or null when
    /// it has no matching closing brace or no comma at its own level.
    /// </summary>
    private static (IReadOnlyList<string> Texts, int Close)? Choices(string word, int open)
    {
        List<string> texts = [];
        int level = 0;
        int start = open + 1;
        for (int i = open + 1; i < word.Length; i++)
        {
            switch (word[i])
            {
                case '{':
                    level++;
                    break;
                case '}' when level > 0:
                    level--;
                    break;
                case '}':
                    if (texts.Count == 0)
                    {
                        return null;
                    }

                    texts.Add(word[start..i]);
                    return (texts, i);
                case ',' when level == 0:
                    texts.Add(word[start..i]);
                    start = i + 1;
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="text"/> with a leading <c>~</c> replaced by the home directory, a leading
    /// <c>~+</c> by <c>$PWD</c> and <c>~-</c> by <c>$OLDPWD</c>, and a leading <c>~NAME</c> by what
    /// <paramref name="userHome"/> gives for the name, and with every <c>$NAME</c> and
    /// <c>${NAME}</c> replaced by what <paramref name="value"/> gives for the name, which is the empty
    /// string for a variable the shell does not define, as the shell expands it.
    /// </summary>
    public static string Expand(string text, Func<string, string> value, string home, Func<string, string> userHome)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(userHome);
        string expanded = text;
        if (text.StartsWith('~'))
        {
            int end = text.IndexOfAny(Separators, 1) is int separator and >= 0
                ? separator
                : text.Length;
            string rest = text[end..];
            expanded = text[1..end] switch
            {
                "" => home + rest,
                "+" => value("PWD") + rest,
                "-" => value("OLDPWD") + rest,
                string name => userHome(name) + rest,
            };
        }

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
    /// The highest directory a pattern or word may reach: <see cref="BeforeWildcard"/>, followed by
    /// one <c>..</c> for each level the part from its first wildcard component on may climb above it.
    /// A component that is <c>..</c>, alone or as one choice of a brace, or that begins with <c>.</c>
    /// followed by a wildcard or a second <c>.</c>, which a shell may match to <c>..</c>, is counted as
    /// a climb; <c>.</c> and an empty component stay where they are, and so does one holding
    /// <c>**</c>, which zsh, and bash with <c>globstar</c>, may match to no directory at all; any other
    /// descends one level. A brace is expanded before this is asked (<see cref="Braces"/>), so a brace
    /// left in the text is one a shell keeps as it is. Everything the pattern may match lies under the directory this names, so judging it
    /// judges them all; a regular expression that only looks like such a pattern names a directory
    /// at or below the one it is resolved against, and is judged by that.
    /// </summary>
    public static string HighestReach(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string before = BeforeWildcard(text);
        int depth = 0;
        int lowest = 0;
        foreach (string component in text[before.Length..].Split(Separators))
        {
            depth += component switch
            {
                "" or "." => 0,
                _ when MayBeParent(component) => -1,
                _ when component.Contains("**", StringComparison.Ordinal) => 0,
                _ => 1,
            };
            lowest = Math.Min(lowest, depth);
        }

        return before + string.Concat(Enumerable.Repeat(".." + Path.DirectorySeparatorChar, -lowest));
    }

    /// <summary>Whether a shell may read a path component as <c>..</c>.</summary>
    private static bool MayBeParent(string component) =>
        component.Split('{', '}', ',').Contains("..")
        || (component.Length > 1 && component[0] == '.' && (component[1] == '.' || Wildcards.Contains(component[1], StringComparison.Ordinal)));

    private static char[] Separators { get; } = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];

    [GeneratedRegex(@"\$(?:\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|(?<bare>[A-Za-z_][A-Za-z0-9_]*))")]
    private static partial Regex Variable();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();

    /// <summary>A place on the directory stack, as <c>pushd</c> and zsh's <c>cd</c> take one.</summary>
    [GeneratedRegex(@"^[+-][0-9]+$")]
    private static partial Regex StackPlace();
}

/// <summary>
/// One command of a shell command line: its words, how many subshells it runs inside, and the
/// operators before and after it. An operator is <c>;</c> (also for a line end), <c>&amp;&amp;</c>,
/// <c>||</c>, <c>|</c>, <c>&amp;</c>, <c>(</c> or <c>)</c> (also for an opening or closing backtick),
/// or empty at the start or end of the line.
/// </summary>
public sealed record ShellCommand(IReadOnlyList<string> Words, int Depth, string Before, string After);

/// <summary>
/// Where a command moves the shell: to <see cref="Destination"/>, as written, for a
/// <see cref="DirectoryMoveKind.To"/>; back to a directory the shell has already been in for a
/// <see cref="DirectoryMoveKind.Back"/>; or to one nothing the guard receives names for a
/// <see cref="DirectoryMoveKind.Unknown"/>.
/// </summary>
public sealed record DirectoryMove(DirectoryMoveKind Kind, string? Destination)
{
    public static DirectoryMove Back { get; } = new(DirectoryMoveKind.Back, null);

    public static DirectoryMove Unknown { get; } = new(DirectoryMoveKind.Unknown, null);

    public static DirectoryMove To(string destination) => new(DirectoryMoveKind.To, destination);
}

/// <summary>The kinds of <see cref="DirectoryMove"/>.</summary>
public enum DirectoryMoveKind
{
    To,
    Back,
    Unknown,
}
