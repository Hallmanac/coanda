using System.Text.Json;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>What the guard answers a call with: its exit code, and the reason when it refuses.</summary>
public sealed record ToolGuardOutcome(int ExitCode, string? Reason)
{
    /// <summary>The exit code with which a pre-tool-use hook lets the call through.</summary>
    public const int AllowCode = 0;

    /// <summary>The exit code with which a pre-tool-use hook denies the call, its standard error being the reason.</summary>
    public const int DenyCode = 2;

    public static ToolGuardOutcome Allowed { get; } = new(AllowCode, null);

    public bool IsAllowed => ExitCode == AllowCode;

    public static ToolGuardOutcome Denied(string reason) => new(DenyCode, reason);
}

/// <summary>
/// The pre-tool-use hook every Claude Code session carries, run as the hidden <c>coa tool-guard</c>
/// subcommand. It reads the call the CLI is about to make from standard input and refuses it when a
/// path the call names is the node home or a Claude Code config directory, is inside one, or
/// contains one, unless the path is the session's own work directory or inside it, or inside the
/// directory the CLI keeps for this session's own conversation. Anything it cannot read or decide is
/// a refusal.
/// </summary>
/// <remarks>
/// The paths a call names are the file path of a Read, Edit, or Write; the path of a Glob or a Grep
/// and the pattern of a Glob; and every word of a Bash command that, once expanded, is absolute,
/// begins with <c>~</c> or <c>.</c>, or holds a path separator, together with the part after the
/// first <c>=</c> of a word that holds one. A relative word is judged against each directory a
/// <c>cd</c> or <c>pushd</c> earlier in the command may have moved to, and also the one it was in
/// before unless the word's command is joined to the <c>cd</c> by <c>&amp;&amp;</c>, and a
/// <c>cd</c>'s destination is itself a path the call names. <c>$PWD</c> is the directory the
/// shell is in at that point; a call that depends on <c>$OLDPWD</c> or <c>cd -</c>, which nothing the guard
/// receives names, is refused. A path holding a wildcard is judged by the highest directory it may
/// reach (<see cref="ShellWords.HighestReach"/>). Files a
/// process the command starts reads on its own are not paths a call names, and stay allowed. The
/// reason never quotes a file's contents: it names the path, the directory it reaches, and nothing else.
/// </remarks>
public static class ToolGuard
{
    /// <summary>The Claude Code tools the hook is registered for, as the settings document's matcher lists them.</summary>
    public static IReadOnlyList<string> GuardedTools { get; } = ["Read", "Edit", "Write", "Glob", "Grep", "Bash"];

    /// <summary>
    /// Runs the guard as <c>coa</c> does: <paramref name="arguments"/> is the whole argument list,
    /// the subcommand first, and <paramref name="input"/> the call's JSON on standard input.
    /// </summary>
    public static ToolGuardOutcome Run(IReadOnlyList<string> arguments, TextReader input, IReadOnlyDictionary<string, string> environment)
    {
        try
        {
            ToolGuardRules? rules = ToolGuardRules.Parse(arguments, out string? problem);
            if (rules is null)
            {
                return ToolGuardOutcome.Denied($"coa refused this call because the tool guard's arguments are malformed: {problem}.");
            }

            string text = input.ReadToEnd();
            JsonDocument call;
            try
            {
                call = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                return ToolGuardOutcome.Denied("coa refused this call because the tool guard could not read it: its input is not JSON.");
            }

            using (call)
            {
                return Decide(rules, call.RootElement, environment);
            }
        }
        catch (Exception failure)
        {
            // The type alone: a message could quote the input, and a Write's input is a file's contents.
            return ToolGuardOutcome.Denied($"coa refused this call because the tool guard failed while deciding it ({failure.GetType().Name}).");
        }
    }

    /// <summary>The decision on one pre-tool-use call, given as the JSON the CLI delivers.</summary>
    public static ToolGuardOutcome Decide(ToolGuardRules rules, JsonElement call, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(environment);
        if (call.ValueKind != JsonValueKind.Object
            || Text(call, "hook_event_name") != "PreToolUse"
            || Text(call, "tool_name") is not { Length: > 0 } tool
            || !call.TryGetProperty("tool_input", out JsonElement toolInput)
            || toolInput.ValueKind != JsonValueKind.Object)
        {
            return Unreadable("it is not a pre-tool-use call naming a tool and its input");
        }

        string work = GuardPath.Resolve(string.Empty, rules.WorkDirectory);
        string cwd = Text(call, "cwd") is { Length: > 0 } reported
            ? GuardPath.Resolve(reported, work)
            : work;
        string home = HomeDirectory(environment);
        IReadOnlyList<string> atCwd = [cwd];
        string pwd = cwd;
        string? unknowable = null;

        IReadOnlyList<Named>? named = tool switch
        {
            "Read" or "Edit" or "Write" => Text(toolInput, "file_path") is { } file
                ? [new(Expanded(file), atCwd)]
                : null,
            "Glob" => Text(toolInput, "pattern") is { } pattern
                ? Glob(pattern, Text(toolInput, "path"))
                : null,
            "Grep" => Text(toolInput, "path") is { } path
                ? [new(Expanded(path), atCwd)]
                : [new(string.Empty, atCwd)],
            "Bash" => Text(toolInput, "command") is { } command
                ? Bash(command)
                : null,
            _ => null,
        };
        if (named is null)
        {
            return Unreadable($"it names the tool '{tool}' without the input the guard judges");
        }

        if (unknowable is not null)
        {
            return ToolGuardOutcome.Denied($"coa refused this call: it depends on {unknowable}, which the tool guard cannot know, so it cannot judge where the call reaches.");
        }

        Judge judge = new(rules, work, ConversationDirectory(rules, call, cwd));
        foreach (Named path in named)
        {
            if (judge.Refusal(path) is { } reason)
            {
                return ToolGuardOutcome.Denied(reason);
            }
        }

        return ToolGuardOutcome.Allowed;

        string Expanded(string text) => ShellWords.Expand(text, Value, home);

        // The shell sets PWD to the directory it is in, whatever the hook inherited, and its OLDPWD is
        // the directory it was in before, which nothing the hook receives names.
        string Value(string name)
        {
            switch (name)
            {
                case "PWD":
                    return pwd;
                case "OLDPWD":
                    unknowable ??= "$OLDPWD, the shell's previous directory";
                    return string.Empty;
                default:
                    return environment.TryGetValue(name, out string? value)
                        ? value
                        : string.Empty;
            }
        }

        List<Named> Glob(string pattern, string? path)
        {
            List<Named> paths = [new(Expanded(pattern), atCwd)];
            if (path is not null)
            {
                // A relative pattern is matched under the Glob's own path, so it is judged there too.
                string under = Expanded(path);
                paths.Add(new(under, atCwd));
                paths.Add(new(Expanded(pattern), [.. GuardPath.Readings(ShellWords.HighestReach(under)).Select(reading => GuardPath.Resolve(reading, cwd))]));
            }

            return paths;
        }

        // A relative word is judged against each directory a cd or pushd earlier in the command may
        // have moved the shell to, or the one the call starts in when there is none. A directory change
        // inside a subshell lasts until the subshell ends. Only the commands joined to a cd by && run
        // where it moved the shell and nowhere else; any other command after it may also run where the
        // shell was, because the cd may have failed, been skipped, or run in a pipeline's or a
        // background job's own process.
        List<Named> Bash(string command)
        {
            List<Named> paths = [];
            List<Level> levels = [new(atCwd, [])];
            foreach (ShellCommand next in ShellWords.Commands(command))
            {
                while (levels.Count > next.Depth + 1)
                {
                    levels.RemoveAt(levels.Count - 1);
                }

                while (levels.Count < next.Depth + 1)
                {
                    levels.Add(new(levels[^1].Bases, []));
                }

                if (next.Before is not ("&&" or "|" or "("))
                {
                    levels[^1] = new(Union(levels[^1].Bases, levels[^1].Unless), []);
                }

                IReadOnlyList<string> at = levels[^1].Bases;
                foreach (string word in next.Words)
                {
                    int equals = word.IndexOf('=', StringComparison.Ordinal);
                    string[] texts = equals >= 0
                        ? [word, word[(equals + 1)..]]
                        : [word];
                    foreach (string text in texts)
                    {
                        foreach (string expanded in ExpandedAt(text, at))
                        {
                            if (ShellWords.IsPathLike(expanded))
                            {
                                paths.Add(new(expanded, at));
                            }
                        }
                    }
                }

                switch (ShellWords.DirectoryChange(next.Words))
                {
                    case null:
                        break;
                    case "-":
                        unknowable ??= "cd -, which returns to the shell's previous directory";
                        break;
                    case { } destination:
                        List<string> moved = [];
                        foreach (string expanded in ExpandedAt(destination, at))
                        {
                            paths.Add(new(expanded, at));
                            moved.AddRange(
                                from reading in GuardPath.Readings(ShellWords.HighestReach(expanded))
                                from directory in at
                                select GuardPath.Resolve(reading, directory));
                        }

                        // A cd that starts a list or follows && and is followed by && moves the shell for
                        // the rest of that && chain; the directories it left come back when the chain ends.
                        levels[^1] = next.Before is "" or ";" or "&&" or "(" or "&" && next.After == "&&"
                            ? new(Union(moved, []), Union(levels[^1].Unless, at))
                            : levels[^1] with { Bases = Union(at, moved) };
                        break;
                }
            }

            return paths;
        }

        // A word as the shell expands it in each directory it may be in, which differs only through $PWD.
        IReadOnlyList<string> ExpandedAt(string text, IReadOnlyList<string> at) =>
            [.. at.Select(directory =>
            {
                pwd = directory;
                return Expanded(text);
            }).Distinct(StringComparer.Ordinal)];

        static IReadOnlyList<string> Union(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
            [.. first.Concat(second).Distinct(GuardPath.Comparer)];
    }

    /// <summary>
    /// The directory the CLI keeps for this session's own conversation, beside its transcript:
    /// <c>&lt;config dir&gt;/projects/&lt;encoded work directory&gt;/&lt;session id&gt;</c>, taken from the
    /// call's <c>transcript_path</c> only when that is <c>&lt;session id&gt;.jsonl</c> directly under a
    /// directory in <c>projects/</c> of the config directory the session runs under. Null otherwise.
    /// </summary>
    private static string? ConversationDirectory(ToolGuardRules rules, JsonElement call, string cwd)
    {
        if (Text(call, "session_id") is not { Length: > 0 } session
            || session is "." or ".."
            || session.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || session.IndexOfAny(['/', '\\']) >= 0
            || Text(call, "transcript_path") is not { Length: > 0 } transcript
            || !string.Equals(Path.GetFileName(transcript), session + ".jsonl", GuardPath.Comparison)
            || Path.GetDirectoryName(Path.Combine(cwd, transcript)) is not { } encoded)
        {
            return null;
        }

        string resolved = GuardPath.Resolve(encoded, cwd);
        string projects = GuardPath.Resolve("projects", GuardPath.Resolve(string.Empty, rules.SessionConfigDirectory));
        return Path.GetDirectoryName(resolved) is { } parent && string.Equals(parent, projects, GuardPath.Comparison)
            ? Path.Combine(resolved, session)
            : null;
    }

    /// <summary>
    /// The directory a shell expands <c>~</c> to: <c>HOME</c>, which Git Bash also reads on Windows,
    /// then <c>USERPROFILE</c> on Windows, then the user's profile directory as .NET finds it.
    /// </summary>
    private static string HomeDirectory(IReadOnlyDictionary<string, string> environment)
    {
        foreach (string name in OperatingSystem.IsWindows() ? new[] { "HOME", "USERPROFILE" } : ["HOME"])
        {
            if (environment.TryGetValue(name, out string? value) && value.Length > 0)
            {
                return value;
            }
        }

        return System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ToolGuardOutcome Unreadable(string why) =>
        ToolGuardOutcome.Denied($"coa refused this call because the tool guard could not read it: {why}.");

    /// <summary>
    /// A path a call names, expanded but not yet resolved, with the directories it is resolved against
    /// when it is relative. It is judged against each of them.
    /// </summary>
    private sealed record Named(string Text, IReadOnlyList<string> Bases);

    /// <summary>
    /// Where the shell may be at one subshell depth: <see cref="Bases"/>, the directories the next
    /// command may run in, and <see cref="Unless"/>, the directories it may be back in once the
    /// <c>&amp;&amp;</c> chain a cd began ends.
    /// </summary>
    private sealed record Level(IReadOnlyList<string> Bases, IReadOnlyList<string> Unless);

    /// <summary>The protected and allowed directories, resolved once per call.</summary>
    private sealed class Judge(ToolGuardRules rules, string work, string? conversation)
    {
        private readonly string home = GuardPath.Resolve(string.Empty, rules.NodeHome);
        private readonly IReadOnlyList<string> configs = [.. rules.ProtectedConfigDirectories.Select(d => GuardPath.Resolve(string.Empty, d))];

        public string? Refusal(Named named)
        {
            // Everything a pattern may match lies under the highest directory it may reach.
            foreach (string reading in GuardPath.Readings(ShellWords.HighestReach(named.Text)))
            {
                foreach (string directory in named.Bases)
                {
                    if (Refusal(GuardPath.Resolve(reading, directory)) is { } reason)
                    {
                        return reason;
                    }
                }
            }

            return null;
        }

        private string? Refusal(string path)
        {
            if (GuardPath.IsWithin(path, work) || (conversation is not null && GuardPath.IsWithin(path, conversation)))
            {
                return null;
            }

            if (Reaches(path, home))
            {
                return $"coa refused this call: the path '{path}' is in the node home ({home}) or contains it, and a session may reach only its own work directory ({work}) there.";
            }

            return configs.FirstOrDefault(config => Reaches(path, config)) is { } reached
                ? $"coa refused this call: the path '{path}' is in a Claude Code config directory ({reached}) or contains it, and a session may reach only its own conversation's directory there."
                : null;
        }

        private static bool Reaches(string path, string directory) =>
            GuardPath.IsWithin(path, directory) || GuardPath.IsWithin(directory, path);
    }
}
