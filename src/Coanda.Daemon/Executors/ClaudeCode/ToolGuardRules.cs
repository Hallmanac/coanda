namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// What a session's tool guard judges by, rendered at the spawn into the hook's argument list: the
/// node home, the session's own work directory, the config directory the session runs under, and
/// every other Claude Code config directory, or config file kept outside one, it may not reach. Every
/// path is absolute. <see cref="Confined"/> says whether the session's agent declares itself
/// confined, which limits it to the work directory and its own conversation's directory and to the
/// tools in <see cref="ToolGuard.ConfinedTools"/>. <see cref="InboxDirectories"/> are the directories
/// Claude Code binds other sessions' inboxes in, which the session may not reach either.
/// <see cref="SkillsRoot"/>, given only for a session whose agent names skills, is the folder the
/// session's skills are written into, which no Edit or Write may reach.
/// </summary>
public sealed record ToolGuardRules(
    string NodeHome,
    string WorkDirectory,
    string SessionConfigDirectory,
    IReadOnlyList<string> ConfigDirectories,
    bool Confined = false,
    IReadOnlyList<string>? InboxDirectories = null)
{
    /// <summary>The hidden <c>coa</c> subcommand that runs the guard.</summary>
    public const string Command = "tool-guard";

    public const string NodeHomeOption = "--node-home";

    public const string WorkOption = "--work";

    public const string SessionConfigOption = "--session-config";

    public const string ConfigOption = "--config";

    public const string InboxOption = "--inbox";

    /// <summary>The option, followed by no value, that marks a confined session's rules.</summary>
    public const string ConfinedOption = "--confined";

    public const string SkillsOption = "--skills";

    /// <summary>The session's skills root, absolute, or null for a session whose agent names no skill.</summary>
    public string? SkillsRoot { get; init; }

    /// <summary>Every config directory the guard protects: the one the session runs under and the others, each once.</summary>
    public IReadOnlyList<string> ProtectedConfigDirectories =>
        [.. new[] { SessionConfigDirectory }.Concat(ConfigDirectories).Distinct(StringComparer.Ordinal)];

    /// <summary>The argument list the hook passes to <c>coa</c>, the subcommand first.</summary>
    public IReadOnlyList<string> Arguments =>
    [
        Command,
        NodeHomeOption, NodeHome,
        WorkOption, WorkDirectory,
        SessionConfigOption, SessionConfigDirectory,
        .. ConfigDirectories.SelectMany(directory => new[] { ConfigOption, directory }),
        .. (InboxDirectories ?? []).SelectMany(directory => new[] { InboxOption, directory }),
        .. SkillsRoot is { } skills ? new[] { SkillsOption, skills } : [],
        .. Confined ? new[] { ConfinedOption } : [],
    ];

    /// <summary>
    /// The rules an argument list carries, or null with the <paramref name="problem"/> when it is not
    /// one <see cref="Arguments"/> renders: the subcommand first, then each option followed by an
    /// absolute path, the node home, the work directory, and the session's config directory exactly
    /// once each, the skills root at most once, and any number of other config directories and inbox
    /// directories, with <see cref="ConfinedOption"/>, which takes no value, at most once among them.
    /// </summary>
    public static ToolGuardRules? Parse(IReadOnlyList<string> arguments, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments is not [Command, ..])
        {
            problem = $"they do not begin with '{Command}'";
            return null;
        }

        string? home = null;
        string? work = null;
        string? session = null;
        string? skills = null;
        List<string> configs = [];
        List<string> inboxes = [];
        bool confined = false;
        for (int index = 1; index < arguments.Count; index += 2)
        {
            string option = arguments[index];
            if (option == ConfinedOption)
            {
                if (confined)
                {
                    problem = "an option that may be given once is given twice";
                    return null;
                }

                // It takes no value, so the next option follows it directly.
                confined = true;
                index--;
                continue;
            }

            if (index + 1 >= arguments.Count)
            {
                problem = $"the option '{option}' has no value";
                return null;
            }

            string value = arguments[index + 1];
            if (value.Length == 0 || !Path.IsPathFullyQualified(value))
            {
                problem = $"the option '{option}' is not followed by an absolute path";
                return null;
            }

            string? repeated = option switch
            {
                NodeHomeOption => Once(ref home, value),
                WorkOption => Once(ref work, value),
                SessionConfigOption => Once(ref session, value),
                SkillsOption => Once(ref skills, value),
                ConfigOption => Add(configs, value),
                InboxOption => Add(inboxes, value),
                _ => $"'{option}' is not an option the guard takes",
            };
            if (repeated is not null)
            {
                problem = repeated;
                return null;
            }
        }

        (problem, ToolGuardRules? rules) = (home, work, session) switch
        {
            (null, _, _) => ($"they name no {NodeHomeOption}", null),
            (_, null, _) => ($"they name no {WorkOption}", null),
            (_, _, null) => ($"they name no {SessionConfigOption}", null),
            ({ } h, { } w, { } s) => ((string?)null, new ToolGuardRules(h, w, s, configs, confined, inboxes) { SkillsRoot = skills }),
        };
        return rules;

        string? Once(ref string? slot, string value)
        {
            if (slot is not null)
            {
                return "an option that may be given once is given twice";
            }

            slot = value;
            return null;
        }

        static string? Add(List<string> list, string value)
        {
            list.Add(value);
            return null;
        }
    }
}
