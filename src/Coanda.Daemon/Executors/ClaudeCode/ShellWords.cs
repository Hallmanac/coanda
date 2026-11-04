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
    /// removed, with how many subshells (parentheses, <c>$(</c>, or backticks) it runs inside, with
    /// the operators on either side of it, and with which of its words are its arguments (see
    /// <see cref="ShellCommand.Arguments"/>).
    /// </summary>
    public static IReadOnlyList<ShellCommand> Commands(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        List<ShellCommand> commands = [];
        List<string> words = [];
        List<string> arguments = [];
        List<string> certain = [];
        var word = new StringBuilder();
        int depth = 0;
        bool backticks = false;
        string before = string.Empty;

        // Redirections and comments are told apart from arguments only in a line with no quotes,
        // backslashes, or ${, where every word this reader makes is a word the shell makes too; in any
        // other line, every word counts as an argument, and a word that may be a redirection's or a
        // comment's is left out of the certain arguments.
        bool plain = command.IndexOfAny(['\'', '"', '\\']) < 0 && !command.Contains("${", StringComparison.Ordinal);

        // Whether the next word is the file of a redirection, and whether the words are in a comment.
        bool operand = false;
        bool comment = false;
        bool commentable = false;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '\\' && LineEndAt(command, i + 1) is int continuation and > 0)
            {
                // A backslash before a line end joins the two lines into one, as a shell does.
                i += continuation;
            }
            else if ((c == '&' && RedirectionAt(command, i - 1)) || (c == '|' && RedirectionAt(command, i - 1) && command[i - 1] == '>'))
            {
                // The & of >& and <&, and the | of >|, belong to the redirection before them: the
                // word after is its descriptor or file, and the command goes on.
            }
            else if (c == '&' && i + 1 < command.Length && command[i + 1] == '>')
            {
                // &> and &>> redirect both outputs, and the command goes on; the > comes next.
                EndWord();
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

                // A comment runs to the end of its line, whatever operators it holds.
                comment = comment && c is not ('\n' or '\r');
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
            else if (Redirections.Contains(c, StringComparison.Ordinal))
            {
                // A digit written right before the redirection names the descriptor it redirects. A
                // number of more digits does in bash but is an argument in zsh, and a {NAME} asks
                // bash and zsh for a new descriptor, so either may or may not be an argument.
                string adjacent = word.ToString();
                bool single = !operand && adjacent.Length == 1 && char.IsAsciiDigit(adjacent[0]);
                bool ambiguous = !operand && ((adjacent.Length > 1 && adjacent.All(char.IsAsciiDigit)) || DescriptorName().IsMatch(adjacent));
                EndWord(plain && single, ambiguous || (!plain && single));
                if (!comment)
                {
                    // <( and >( are a process substitution, which the shell passes on as a file name.
                    if (i + 1 < command.Length && command[i + 1] == '(')
                    {
                        arguments.Add(c + "(");
                        if (plain)
                        {
                            certain.Add(c + "(");
                        }

                        operand = false;
                    }
                    else
                    {
                        operand = true;
                    }
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                EndWord();
            }
            else if (c is not ('\'' or '"'))
            {
                // Only a # that begins a word after whitespace or at the start of the line begins a
                // comment; after an operator it may be zsh's glob flag, as in (#i).
                commentable = word.Length > 0
                    ? commentable
                    : i == 0 || char.IsWhiteSpace(command[i - 1]);
                word.Append(c);
            }
        }

        EndWord();
        EndCommand(string.Empty);
        return commands;

        // Whether a redirection is at this position: a < or a > with no backslash escaping it.
        static bool RedirectionAt(string text, int at)
        {
            if (at < 0 || !Redirections.Contains(text[at], StringComparison.Ordinal))
            {
                return false;
            }

            int backslashes = 0;
            while (at - backslashes - 1 >= 0 && text[at - backslashes - 1] == '\\')
            {
                backslashes++;
            }

            return backslashes % 2 == 0;
        }

        static int LineEndAt(string text, int at) => text.AsSpan(at) switch
        {
            ['\r', '\n', ..] => 2,
            ['\n', ..] or ['\r', ..] => 1,
            _ => 0,
        };

        // A word ends: a descriptor is not an argument, and a doubtful word may or may not be one.
        void EndWord(bool descriptor = false, bool doubtful = false)
        {
            if (word.Length == 0)
            {
                return;
            }

            string text = word.ToString();
            words.Add(text);
            word.Clear();
            bool redirected = operand || descriptor;
            operand = false;
            if (!redirected && !comment)
            {
                comment = commentable && text[0] == '#';
            }

            // In a plain line a redirection's word or a comment's is surely not an argument; in any
            // other line it may be one, since the < or the # may have been quoted.
            bool argument = !plain || (!redirected && !comment);
            if (argument)
            {
                arguments.Add(text);
            }

            if (argument && !doubtful && (plain || !(redirected || comment)))
            {
                certain.Add(text);
            }
        }

        void EndCommand(string after)
        {
            if (words.Count > 0)
            {
                commands.Add(new ShellCommand([.. words], backticks ? depth + 1 : depth, before, after, [.. arguments], [.. certain]));
                words.Clear();
            }

            arguments.Clear();
            certain.Clear();
            operand = false;
            before = after;
        }
    }

    /// <summary>
    /// How a command changes the shell's directory, when its first word after any leading
    /// <c>NAME=value</c> assignments, shell keywords (<c>then</c>, <c>do</c>, <c>{</c>, <c>!</c>, and
    /// the like), and the wrappers <c>builtin</c>, <c>command</c>, <c>eval</c>, <c>time</c>, <c>noglob</c>, and
    /// <c>nocorrect</c> is <c>cd</c>, <c>pushd</c>, or <c>popd</c>, read with its backslashes removed:
    /// to its one operand (<see cref="DirectoryMove.To"/>), which is <c>-</c> for <c>cd -</c> and
    /// <c>~</c> for a <c>cd</c> with none; back to a directory the shell has been in
    /// (<see cref="DirectoryMove.Back"/>) for a <c>popd</c>, a <c>pushd</c> with no directory, and a
    /// <c>pushd</c> to a place on the directory stack such as <c>+1</c>; and to a directory the guard
    /// cannot know (<see cref="DirectoryMove.Unknown"/>) for zsh's <c>cd OLD NEW</c> and <c>pushd OLD NEW</c>,
    /// which replace part of the current directory's name, for a <c>cd</c> to <c>+1</c> or <c>-1</c>, which zsh reads
    /// as a place on the stack and bash as a directory's name, for a move into a process
    /// substitution, and for a command whose words read as a different move depending on whether
    /// its doubtful words are arguments (see <see cref="ShellCommand.Certain"/>). An option is a word
    /// of <c>-</c> and option letters zsh's <c>cd</c> knows, before any <c>--</c>; any other word, such
    /// as <c>-x</c> or <c>+x</c>, is an operand, because zsh's <c>cd</c> takes a word with a letter it
    /// does not know as a directory, and a word of letters only bash knows, such as <c>-e</c>, is a
    /// move the guard cannot know. Null for any other command.
    /// </summary>
    public static DirectoryMove? DirectoryChange(ShellCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        // A redirection's words and a comment's are not the command's own, so they are left out here;
        // a word that may or may not be one is read both ways, and the two must agree.
        DirectoryMove? read = Read(command.Arguments);
        DirectoryMove? certain = Read(command.Certain);
        return read == certain
            ? read
            : DirectoryMove.Unknown("a directory change whose words the tool guard cannot tell apart from a redirection or a comment");

        static DirectoryMove? Read(IReadOnlyList<string> words)
        {
            int first = NameAt(words);
            string? name = first < words.Count
                ? Unescape(words[first])
                : null;
            // zsh's chdir is its cd under another name.
            name = name is "chdir"
                ? "cd"
                : name;
            if (name is not ("cd" or "pushd" or "popd"))
            {
                return null;
            }

            // The options end at the first word that is not one, or at a --, which is dropped. A word
            // of letters only bash knows is an option to bash and a directory to zsh. Each is read as
            // the shell reads it, with its backslashes removed, so -\P is the option -P.
            int next = first + 1;
            while (next < words.Count && Option().IsMatch(Unescape(words[next])))
            {
                next++;
            }

            if (next < words.Count && BashOption().IsMatch(Unescape(words[next])))
            {
                return DirectoryMove.Unknown($"{name} {words[next]}, which bash reads as an option and zsh as the name of a directory");
            }

            bool dashes = next < words.Count && Unescape(words[next]) == "--";
            string[] operands = [.. words.Skip(dashes ? next + 1 : next)];
            return (name, operands) switch
            {
                ("popd", _) or (not "cd", []) => DirectoryMove.Back,
                ("cd", []) => DirectoryMove.To("~"),
                (_, [_, _, ..]) => DirectoryMove.Unknown($"a {name} with two directories, which zsh reads as a replacement in the name of the directory the shell is in"),
                ("cd", [{ } place]) when StackPlace().IsMatch(Unescape(place)) => DirectoryMove.Unknown($"cd {place}, which zsh reads as a place on the directory stack and bash as the name of a directory"),
                (_, [{ } place, ..]) when StackPlace().IsMatch(Unescape(place)) => dashes
                    ? DirectoryMove.Unknown($"pushd -- {place}, which may name a place on the directory stack or a directory")
                    : DirectoryMove.Back,
                (_, [['<' or '>', '(', ..], ..]) => DirectoryMove.Unknown("a directory change into a process substitution"),
                (_, [{ } destination, ..]) when Unescape(destination) == "-" => DirectoryMove.To("-"),
                (_, [{ } destination, ..]) => DirectoryMove.To(destination),
            };
        }
    }

    /// <summary>
    /// Why <paramref name="command"/> may change where a later <c>cd</c> or <c>pushd</c> goes in a way
    /// the guard does not follow, or null. A word naming <c>CDPATH</c>, or zsh's <c>cdpath</c> tied to
    /// it, may set the directories a <c>cd</c> searches for a relative directory. A word naming the
    /// option <c>AUTO_CD</c>, <c>CDABLE_VARS</c>, or <c>PUSHD_TO_HOME</c>, in any case and with any
    /// underscores, may turn one on, as may the letters <c>J</c>, <c>T</c>, and <c>D</c> zsh's
    /// <c>set</c>, <c>setopt</c>, and <c>unsetopt</c> take for them. An assignment whose name holds an
    /// expansion, or a <c>${(P)…}</c> that may assign to the variable another names, may set either
    /// without naming it. Each word is read with its quotes and backslashes removed.
    /// <para>
    /// Only the shell sets its own variables and options, so a word that merely mentions one of those
    /// names is let through when it is the name or an argument of a program the shell starts: a
    /// command whose name is written out plainly and is not a shell builtin or keyword, not behind an
    /// <c>eval</c>, which would run the arguments as shell, not opened by a <c>(</c>, which may be the
    /// inside of an arithmetic <c>((</c> or <c>$((</c>, and with no <c>$</c> or backtick in any of its
    /// words, on a line that <paramref name="definesCommands"/> says defines no function or alias that
    /// name could call. So
    /// <c>grep -rn CDPATH src</c> and a <c>git commit -m</c> that mentions it are allowed, while a word
    /// before the command's name (an assignment), a word holding an expansion (which may assign, as
    /// <c>${CDPATH:=x}</c> does), and any word of a builtin are still refused.
    /// </para>
    /// </summary>
    public static string? DirectoryRuleChange(ShellCommand command, bool definesCommands)
    {
        ArgumentNullException.ThrowIfNull(command);
        int name = NameAt(command.Words);
        // A command opened by a ( may be the inside of an arithmetic (( or $((, a subshell, a process
        // substitution, or a zsh glob qualifier, any of which the shell evaluates itself; and a command
        // with a $ anywhere in it may hold an arithmetic $[ or a subscript that assigns, split across
        // words. Neither is read as a program.
        bool program = !definesCommands
            && command.Before != "("
            && name < command.Words.Count
            && IsProgram(Plain(command.Words[name]))
            && !command.Words.Take(name).Any(word => Plain(word) == "eval")
            && !command.Words.Any(word => Plain(word).IndexOfAny(['$', '`']) >= 0);
        bool setter = false;
        for (int i = 0; i < command.Words.Count; i++)
        {
            string plain = Plain(command.Words[i]);
            bool mention = program && i >= name;
            string folded = plain.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (!mention && folded.Contains("cdpath", StringComparison.Ordinal))
            {
                return "CDPATH, the directories a cd searches for a relative directory";
            }

            if (!mention && DirectoryOptions.Any(option => folded.Contains(option, StringComparison.Ordinal)))
            {
                return "a shell option that changes where a cd or pushd goes";
            }

            if (DynamicAssignment().IsMatch(plain) || IndirectExpansion().IsMatch(plain))
            {
                return "an assignment to a variable whose name the shell expands";
            }

            if (program)
            {
                continue;
            }

            if (plain is "set" or "setopt" or "unsetopt")
            {
                setter = true;
            }
            else if (setter && OptionLetters().IsMatch(plain))
            {
                return $"{plain}, which may turn on a shell option that changes where a cd or pushd goes";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="command"/>, a line of shell, may define a function or an alias a later
    /// command's name calls: it holds a <c>()</c>, with or without space inside, or the word
    /// <c>function</c> or <c>alias</c>.
    /// </summary>
    public static bool DefinesCommands(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CommandDefinition().IsMatch(command);
    }

    /// <summary>
    /// Whether a command named <paramref name="name"/> is a program the shell starts rather than one it
    /// runs itself: its name holds no expansion, quote, or other character a shell gives a meaning, and
    /// is not a builtin or keyword of bash or zsh.
    /// </summary>
    private static bool IsProgram(string name) =>
        ProgramName().IsMatch(name) && !ShellBuiltins.Contains(name);

    /// <summary>
    /// The index of a command's name among <paramref name="words"/>: the first word after any leading
    /// <c>NAME=value</c> assignments, shell keywords, and wrappers, as <see cref="DirectoryChange"/> reads them.
    /// </summary>
    private static int NameAt(IReadOnlyList<string> words)
    {
        int first = 0;
        while (first < words.Count && (Assignment().IsMatch(words[first]) || Prefixes.Contains(Unescape(words[first]))))
        {
            // command -p and time -p take an option before the command they run; command -v only prints it.
            first += Unescape(words[first]) is "command" or "time" && first + 1 < words.Count && Unescape(words[first + 1]) == "-p"
                ? 2
                : 1;
        }

        return first;
    }

    /// <summary><paramref name="word"/> with its backslashes and any quote left in it removed.</summary>
    private static string Plain(string word) =>
        Unescape(word).Replace("\"", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Whether a <c>cd</c> or <c>pushd</c> may look <paramref name="destination"/>, once expanded, up
    /// in <c>CDPATH</c>: it is relative and does not begin with a <c>.</c> or <c>..</c> component.
    /// </summary>
    public static bool IsSearched(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length == 0 || Path.IsPathRooted(destination) || destination[0] == '~')
        {
            return false;
        }

        string first = destination.Split(Separators)[0];
        return first is not ("." or "..");
    }

    /// <summary>The options, folded to lower case without underscores, that change where a cd or pushd goes.</summary>
    private static IReadOnlyList<string> DirectoryOptions { get; } = ["autocd", "cdablevars", "pushdtohome"];

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

    /// <summary>
    /// The builtins and reserved words of bash and zsh, any of which the shell runs itself and so may
    /// set one of its own variables or options.
    /// </summary>
    private static FrozenSet<string> ShellBuiltins { get; } = FrozenSet.Create(
        StringComparer.Ordinal,
        ".", ":", "[", "[[", "]]", "{", "}", "!", "alias", "autoload", "bg", "bind", "bindkey", "break", "builtin", "bye", "caller",
        "case", "cd", "chdir", "command", "compadd", "comparguments", "compcall", "compctl", "compdef", "compdescribe", "compfiles",
        "compgen", "compgroups", "complete", "compopt", "compquote", "comptags", "comptry", "compvalues", "continue", "coproc",
        "declare", "dirs", "disable", "disown", "do", "done", "echo", "echotc", "echoti", "elif", "else", "emulate", "enable", "end", "esac",
        "eval", "exec", "exit", "export", "false", "fc", "fg", "fi", "float", "for", "foreach", "function", "functions", "getcap",
        "getln", "getopts", "hash", "help", "history", "if", "in", "integer", "jobs", "kill", "let", "limit", "local", "log", "logout",
        "mapfile", "noglob", "nocorrect", "popd", "print", "printf", "private", "pushd", "pushln", "pwd", "r", "read", "readarray",
        "readonly", "rehash", "repeat", "return", "sched", "select", "set", "setcap", "setopt", "shift", "shopt", "source", "stat",
        "suspend", "test", "then", "time", "times", "trap", "true", "ttyctl", "type", "typeset", "ulimit", "umask", "unalias", "unfunction",
        "unhash", "unlimit", "unset", "unsetopt", "until", "vared", "wait", "whence", "where", "which", "while", "zcompile", "zformat",
        "zftp", "zle", "zmodload", "zparseopts", "zprof", "zpty", "zregexparse", "zsocket", "zstat", "zstyle", "ztcp");

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

    /// <summary>
    /// An option of cd or pushd as zsh reads one: a - followed only by letters zsh's cd knows. zsh
    /// reads a word with any other letter as a directory; bash refuses the letters it does not know, so
    /// the cd fails and moves nothing.
    /// </summary>
    [GeneratedRegex(@"^-[qsLP]+$")]
    private static partial Regex Option();

    /// <summary>A word bash reads as an option of cd or pushd and zsh does not: one holding -e, -@, or -n.</summary>
    [GeneratedRegex(@"^-[qsLPe@n]+$")]
    private static partial Regex BashOption();

    /// <summary>A {NAME} written right before a redirection, which asks the shell for a new descriptor.</summary>
    [GeneratedRegex(@"^\{[A-Za-z_][A-Za-z0-9_]*\}$")]
    private static partial Regex DescriptorName();

    /// <summary>A place on the directory stack, as <c>pushd</c> and zsh's <c>cd</c> take one.</summary>
    [GeneratedRegex(@"^[+-][0-9]+$")]
    private static partial Regex StackPlace();

    /// <summary>An assignment whose name holds a <c>$</c>, such as <c>${v}PATH=x</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_]*\$(?:\{[A-Za-z0-9_]+\}|[A-Za-z0-9_]+)[A-Za-z0-9_${}]*=")]
    private static partial Regex DynamicAssignment();

    /// <summary>A zsh expansion with the P flag, such as <c>${(P)v::=x}</c>, which reads or assigns the variable another names.</summary>
    [GeneratedRegex(@"\$\{\([^)]*P")]
    private static partial Regex IndirectExpansion();

    /// <summary>A command name written out plainly: letters, digits, and the punctuation a program's name or path holds.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_./+-]+$")]
    private static partial Regex ProgramName();

    /// <summary>What may define a command a later name calls: a <c>()</c>, or the word <c>function</c> or <c>alias</c>.</summary>
    [GeneratedRegex(@"\(\s*\)|(?<![A-Za-z0-9_])(?:function|alias)(?![A-Za-z0-9_])")]
    private static partial Regex CommandDefinition();

    /// <summary>Single-letter options, among them zsh's J (AUTO_CD), T (CDABLE_VARS), or D (PUSHD_TO_HOME).</summary>
    [GeneratedRegex(@"^[+-][A-Za-z]*[JTD]")]
    private static partial Regex OptionLetters();
}

/// <summary>
/// One command of a shell command line: its words, how many subshells it runs inside, the
/// operators before and after it, and its arguments. An operator is <c>;</c> (also for a line end),
/// <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>&amp;</c>, <c>(</c> or <c>)</c> (also for an opening or
/// closing backtick), or empty at the start or end of the line. The arguments are the words the
/// command receives: its words without a redirection's descriptor number and file and without a
/// comment's words, with a process substitution standing as <c>&lt;(</c> or <c>&gt;(</c>. In a line
/// holding a quote, a backslash, or <c>${</c>, the reader cannot tell those apart, so the arguments
/// are all the words. <see cref="Certain"/> is the arguments without each word that may not be one:
/// in such a line, a redirection's word, a comment's, a process substitution, and a number or a
/// <c>{NAME}</c> written right before a redirection; in any line, a number of more than one digit or a
/// <c>{NAME}</c> written right before a redirection, which a shell may read as its descriptor.
/// </summary>
public sealed record ShellCommand(IReadOnlyList<string> Words, int Depth, string Before, string After, IReadOnlyList<string> Arguments, IReadOnlyList<string> Certain);

/// <summary>
/// Where a command moves the shell: to <see cref="Destination"/>, as written, for a
/// <see cref="DirectoryMoveKind.To"/>; back to a directory the shell has already been in for a
/// <see cref="DirectoryMoveKind.Back"/>; or, for a <see cref="DirectoryMoveKind.Unknown"/>, to one
/// nothing the guard receives names, for the reason <see cref="Destination"/> gives.
/// </summary>
public sealed record DirectoryMove(DirectoryMoveKind Kind, string? Destination)
{
    public static DirectoryMove Back { get; } = new(DirectoryMoveKind.Back, null);

    public static DirectoryMove Unknown(string reason) => new(DirectoryMoveKind.Unknown, reason);

    public static DirectoryMove To(string destination) => new(DirectoryMoveKind.To, destination);
}

/// <summary>The kinds of <see cref="DirectoryMove"/>.</summary>
public enum DirectoryMoveKind
{
    To,
    Back,
    Unknown,
}
