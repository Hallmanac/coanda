namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// What a session's tool guard judges by, rendered at the spawn into the hook's argument list: the
/// node home, the session's own work directory, the config directory the session runs under, and
/// every other Claude Code config directory it may not reach. Every path is absolute.
/// </summary>
public sealed record ToolGuardRules(
    string NodeHome,
    string WorkDirectory,
    string SessionConfigDirectory,
    IReadOnlyList<string> ConfigDirectories)
{
    /// <summary>The hidden <c>coa</c> subcommand that runs the guard.</summary>
    public const string Command = "tool-guard";

    public const string NodeHomeOption = "--node-home";

    public const string WorkOption = "--work";

    public const string SessionConfigOption = "--session-config";

    public const string ConfigOption = "--config";

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
    ];

    /// <summary>
    /// The rules an argument list carries, or null with the <paramref name="problem"/> when it is not
    /// one <see cref="Arguments"/> renders: the subcommand first, then each option followed by an
    /// absolute path, the node home, the work directory, and the session's config directory exactly
    /// once each, and any number of other config directories.
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
        List<string> configs = [];
        for (int index = 1; index < arguments.Count; index += 2)
        {
            string option = arguments[index];
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
                ConfigOption => Add(configs, value),
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
            ({ } h, { } w, { } s) => ((string?)null, new ToolGuardRules(h, w, s, configs)),
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
