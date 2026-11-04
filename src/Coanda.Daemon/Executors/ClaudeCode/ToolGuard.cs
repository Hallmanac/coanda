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
/// first <c>=</c> of a word that holds one. Files a process the command starts reads on its own are
/// not paths a call names, and stay allowed. The reason never quotes a file's contents: it names
/// the path, the directory it reaches, and nothing else.
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

        IReadOnlyList<string>? named = tool switch
        {
            "Read" or "Edit" or "Write" => Text(toolInput, "file_path") is { } file
                ? [Expanded(file)]
                : null,
            "Glob" => Text(toolInput, "pattern") is { } pattern
                ? Glob(pattern, Text(toolInput, "path"))
                : null,
            "Grep" => Text(toolInput, "path") is { } path
                ? [Expanded(path)]
                : [string.Empty],
            "Bash" => Text(toolInput, "command") is { } command
                ? Bash(command)
                : null,
            _ => null,
        };
        if (named is null)
        {
            return Unreadable($"it names the tool '{tool}' without the input the guard judges");
        }

        Judge judge = new(rules, work, ConversationDirectory(rules, call, cwd));
        foreach (string path in named)
        {
            string resolved = GuardPath.Resolve(ShellWords.BeforeWildcard(path), cwd);
            if (judge.Refusal(resolved) is { } reason)
            {
                return ToolGuardOutcome.Denied(reason);
            }
        }

        return ToolGuardOutcome.Allowed;

        string Expanded(string text) => ShellWords.Expand(text, environment, home);

        List<string> Glob(string pattern, string? path)
        {
            List<string> paths = [Expanded(pattern)];
            if (path is not null)
            {
                // A relative pattern is matched under the Glob's own path, so it is judged there too.
                string under = Expanded(path);
                paths.Add(under);
                paths.Add(Path.Combine(GuardPath.Resolve(ShellWords.BeforeWildcard(under), cwd), Expanded(pattern)));
            }

            return paths;
        }

        List<string> Bash(string command)
        {
            List<string> paths = [];
            foreach (string word in ShellWords.Split(command))
            {
                string expanded = Expanded(word);
                if (ShellWords.IsPathLike(expanded))
                {
                    paths.Add(expanded);
                }

                int equals = word.IndexOf('=', StringComparison.Ordinal);
                if (equals >= 0 && Expanded(word[(equals + 1)..]) is { } value && ShellWords.IsPathLike(value))
                {
                    paths.Add(value);
                }
            }

            return paths;
        }
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

    /// <summary>The protected and allowed directories, resolved once per call.</summary>
    private sealed class Judge(ToolGuardRules rules, string work, string? conversation)
    {
        private readonly string home = GuardPath.Resolve(string.Empty, rules.NodeHome);
        private readonly IReadOnlyList<string> configs = [.. rules.ProtectedConfigDirectories.Select(d => GuardPath.Resolve(string.Empty, d))];

        public string? Refusal(string path)
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
