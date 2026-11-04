using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Coanda.Core.Agents;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// How an agent definition becomes a headless Claude Code process: the tables the adapter maps
/// through, the arguments every process of a session carries, and the environment it runs in.
/// Everything here is pure, so the rendering can be read and tested apart from the process it starts.
/// </summary>
/// <remarks>
/// Confirmed against Claude Code 2.1.318. Print mode with stream-json output requires
/// <c>--verbose</c>. <c>--permission-mode dontAsk</c> with <c>--permission-prompts none</c> denies
/// any call that would need a permission answer instead of waiting for one, whatever mode the
/// operator's own settings name. The tools the adapter pre-approves are approved by permission
/// rules in the settings document (<c>permissions.allow</c>), not by <c>--allowedTools</c>, so a CLI
/// that skips the document approves none of them and refuses every call that needs approval; the
/// calls it runs without approval are read-only ones that stay inside the work directory.
/// <c>--setting-sources ""</c> loads no user, project, or local
/// settings (and with them no hooks and no CLAUDE.md), <c>--strict-mcp-config</c> with no
/// <c>--mcp-config</c> loads no MCP server, and the CLI still turns on its automatic memory under
/// the operator's config directory unless the settings document says <c>autoMemoryEnabled</c> is false.
/// The settings document also carries the session's tool guard (<see cref="ToolGuard"/>) as an
/// exec-form pre-tool-use hook that blocks the call when it cannot run, and sets
/// <c>disableAllHooks</c> to false, which outranks a user, project, or local settings file that
/// would switch hooks off; only managed settings rank above it.
/// The settings document sets <c>attribution</c> to <c>{ "commit": "", "pr": "" }</c>, which the
/// 2.1.318 settings schema documents as hiding the attribution Claude Code otherwise adds to the
/// commits and pull requests a session makes (an empty string hides it). It uses the object form
/// rather than <c>false</c>, which older versions reject, and a settings document that fails
/// validation is not loaded at all. It sets no <c>includeCoAuthoredBy</c>, which that schema marks
/// as deprecated, and no <c>sessionUrl</c>, which applies only to web and Remote Control sessions.
/// Managed settings rank above the document here too, so an operator's managed policy can turn
/// attribution back on. The document also registers the <see cref="SettingsEvidence"/> hook, whose
/// output on the process's stream shows that the CLI applied it.
/// </remarks>
public static partial class ClaudeCodeInvocation
{
    /// <summary>The vendor name in an agent's model spec that this adapter runs.</summary>
    public const string Vendor = "claude-code";

    public const string PermissionMode = "dontAsk";

    public const string PermissionPrompts = "none";

    /// <summary>The variable the CLI reads its config directory, and with it its login, from.</summary>
    public const string ConfigDirectoryVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>The setting that turns the CLI's automatic memory off.</summary>
    public const string AutoMemorySetting = "autoMemoryEnabled";

    public const string ApiKeyHelperSetting = "apiKeyHelper";

    /// <summary>The setting that names the attribution added to a session's commits and pull requests; the settings document hides both.</summary>
    public const string AttributionSetting = "attribution";

    /// <summary>The setting whose <c>allow</c> rules pre-approve the session's tools.</summary>
    public const string PermissionsSetting = "permissions";

    /// <summary>The setting that switches every hook off when true; the settings document sets it to false.</summary>
    public const string DisableAllHooksSetting = "disableAllHooks";

    /// <summary>The CLI's own config directory under the user's home when <see cref="ConfigDirectoryVariable"/> is unset.</summary>
    public const string DefaultConfigDirectoryName = ".claude";

    /// <summary>
    /// The files the CLI keeps beside <see cref="DefaultConfigDirectoryName"/> in the user's home when
    /// <see cref="ConfigDirectoryVariable"/> is unset: its global config, which holds the account, the
    /// user's MCP servers, and any stored API key, and that file's backup. With the variable set they
    /// live inside the config directory instead.
    /// </summary>
    public static IReadOnlyList<string> DefaultConfigFileNames { get; } = [".claude.json", ".claude.json.backup"];

    /// <summary>The one mapping from the vendor-neutral tool catalog to Claude Code's own tools.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Tools { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [ToolCatalog.Read] = ["Read"],
        [ToolCatalog.Search] = ["Glob", "Grep"],
        [ToolCatalog.Edit] = ["Edit", "Write"],
        [ToolCatalog.Shell] = ["Bash"],
        [ToolCatalog.Web] = ["WebFetch", "WebSearch"],
    };

    /// <summary>
    /// The variables an enclosing Claude Code session sets for itself. None of them reaches a
    /// session's process, so a daemon started from inside Claude Code does not make its sessions
    /// believe they are that session's children.
    /// </summary>
    public static IReadOnlyList<string> EnclosingSessionVariables { get; } =
    [
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN",
        "CLAUDE_EFFORT",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_EXECPATH",
        "CLAUDE_PID",
        "AI_AGENT",
    ];

    /// <summary>
    /// The credential variables the CLI would use in preference to a credential profile. A spawn
    /// that carries a profile runs with none of them, so the profile is the credential it uses.
    /// </summary>
    public static IReadOnlyList<string> CredentialVariables { get; } =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "CLAUDE_CODE_OAUTH_REFRESH_TOKEN",
        "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR",
        "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR",
    ];

    /// <summary>
    /// The variables that make Claude Code 2.1.318 skip hooks when its environment holds them:
    /// <c>CLAUDE_CODE_SAFE_MODE</c> (what <c>--safe-mode</c> sets) and <c>CLAUDE_CODE_SIMPLE</c> (what
    /// <c>--bare</c> sets). None of them reaches a session, whatever the daemon inherited, so the
    /// session's tool guard always runs.
    /// </summary>
    public static IReadOnlyList<string> HookSkippingVariables { get; } =
    [
        "CLAUDE_CODE_SAFE_MODE",
        "CLAUDE_CODE_SIMPLE",
    ];

    /// <summary>The Claude Code tools an agent's catalog names map to, in catalog order, each once.</summary>
    public static IReadOnlyList<string> ToolsOf(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return [.. ToolCatalog.Names.Where(agent.HoldsTool).SelectMany(name => Tools[name])];
    }

    /// <summary>
    /// Refuses an agent this adapter cannot run: one whose vendor is not <see cref="Vendor"/>, or
    /// whose model or effort is not a plain token, which could otherwise be read as another argument.
    /// </summary>
    public static void Require(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (!string.Equals(agent.Model.Vendor, Vendor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' names the vendor '{agent.Model.Vendor}', and this executor runs only agents of the vendor '{Vendor}'.");
        }

        RequirePlain(agent, "model", agent.Model.Model);
        RequirePlain(agent, "effort", agent.Model.Effort);
    }

    /// <summary>
    /// The settings document every process of a session receives through <c>--settings</c>: automatic
    /// memory off, no attribution on commits or pull requests, a helper profile's API key helper,
    /// permission rules that pre-approve each of <paramref name="tools"/> (none when it is empty),
    /// hooks left on, the <see cref="SettingsEvidence"/> hook registered for every session start, and
    /// the tool guard registered before every call to the six tools it judges. Both hooks run as
    /// <paramref name="coaExecutable"/> with their arguments and no shell, and block when they cannot run.
    /// </summary>
    public static string Settings(CredentialProfile? profile, ToolGuardRules guard, string coaExecutable, IReadOnlyList<string> tools)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(coaExecutable);
        var settings = new JsonObject
        {
            [AutoMemorySetting] = false,
            [AttributionSetting] = new JsonObject { ["commit"] = string.Empty, ["pr"] = string.Empty },
        };
        if (profile?.ApiKeyHelper is { } helper)
        {
            settings[ApiKeyHelperSetting] = helper;
        }

        if (tools.Count > 0)
        {
            settings[PermissionsSetting] = new JsonObject { ["allow"] = Strings(tools) };
        }

        settings[DisableAllHooksSetting] = false;
        settings["hooks"] = new JsonObject
        {
            [SettingsEvidence.HookEvent] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(Hook(coaExecutable, SettingsEvidence.Arguments)),
            }),
            ["PreToolUse"] = new JsonArray(new JsonObject
            {
                ["matcher"] = string.Join('|', ToolGuard.GuardedTools),
                ["hooks"] = new JsonArray(Hook(coaExecutable, guard.Arguments)),
            }),
        };
        return settings.ToJsonString();
    }

    /// <summary>One exec-form hook entry: <paramref name="coaExecutable"/> run with the arguments and no shell, blocking when it cannot run.</summary>
    private static JsonObject Hook(string coaExecutable, IReadOnlyList<string> arguments) => new()
    {
        ["type"] = "command",
        ["command"] = coaExecutable,
        ["args"] = Strings(arguments),
        ["onFailure"] = "block",
    };

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    /// <summary>
    /// The config directory a session's CLI uses when its spawn sets none: the
    /// <see cref="ConfigDirectoryVariable"/> the daemon inherited, resolved against the work directory
    /// the CLI starts in, or, when that is unset, <see cref="DefaultConfigDirectoryName"/> under the
    /// inherited home directory.
    /// </summary>
    public static string DefaultConfigDirectory(IReadOnlyDictionary<string, string> inherited, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        if (inherited.TryGetValue(ConfigDirectoryVariable, out string? configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured, Path.GetFullPath(workingDirectory));
        }

        return Path.GetFullPath(Path.Combine(InheritedHome(inherited), DefaultConfigDirectoryName));
    }

    /// <summary>
    /// The config files a session's CLI keeps outside any config directory: each of
    /// <see cref="DefaultConfigFileNames"/> under the inherited home directory when the daemon inherited
    /// no <see cref="ConfigDirectoryVariable"/>, and none when it did.
    /// </summary>
    public static IReadOnlyList<string> DefaultConfigFiles(IReadOnlyDictionary<string, string> inherited)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        if (inherited.TryGetValue(ConfigDirectoryVariable, out string? configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        string home = InheritedHome(inherited);
        return [.. DefaultConfigFileNames.Select(name => Path.GetFullPath(Path.Combine(home, name)))];
    }

    /// <summary>The home directory the CLI reads its default config from: <c>USERPROFILE</c> on Windows, <c>HOME</c> elsewhere, or the user's profile directory as .NET finds it.</summary>
    private static string InheritedHome(IReadOnlyDictionary<string, string> inherited) =>
        inherited.TryGetValue(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", out string? given) && !string.IsNullOrWhiteSpace(given)
            ? given
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The rules of a session's tool guard: the node home, the session's work directory, the config
    /// directory it runs under (its profile's, or the default one), and every other config directory
    /// it may not reach: the default one, whatever profile the spawn carries, the files the CLI keeps
    /// beside the default one in the home directory (<see cref="DefaultConfigFiles"/>), and each of
    /// <paramref name="profileDirectories"/>, the config directories of the node's profiles.
    /// </summary>
    public static ToolGuardRules Guard(
        NodeHome home,
        string workingDirectory,
        CredentialProfile? profile,
        IReadOnlyDictionary<string, string> inherited,
        IEnumerable<string> profileDirectories)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(profileDirectories);
        string work = Path.GetFullPath(workingDirectory);
        string defaultDirectory = DefaultConfigDirectory(inherited, work);
        string session = profile?.ConfigDirectory is { } own
            ? Path.GetFullPath(own)
            : defaultDirectory;
        IReadOnlyList<string> others = [.. new[] { defaultDirectory }
            .Concat(DefaultConfigFiles(inherited))
            .Concat(profileDirectories.Select(d => Path.GetFullPath(d)))
            .Where(d => !string.Equals(d, session, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)];
        return new ToolGuardRules(Path.GetFullPath(home.Path), work, session, others);
    }

    /// <summary>
    /// The arguments every process of a session carries, spawn and resume alike, since the CLI keeps
    /// none of them with the session: the persona file, the model and effort, the tools, the
    /// permission mode, no MCP servers, no setting sources, and the settings document, which carries
    /// the tools' pre-approval, the evidence that it was applied, and the session's tool guard.
    /// </summary>
    public static IReadOnlyList<string> SessionArguments(AgentDefinition agent, CredentialProfile? profile, ToolGuardRules guard, string coaExecutable)
    {
        IReadOnlyList<string> tools = ToolsOf(agent);
        return
        [
            "--append-system-prompt-file", SessionFiles.PersonaFile,
            "--model", agent.Model.Model,
            "--effort", agent.Model.Effort,
            "--tools", string.Join(',', tools),
            "--permission-mode", PermissionMode,
            "--permission-prompts", PermissionPrompts,
            "--strict-mcp-config",
            "--setting-sources", string.Empty,
            "--settings", Settings(profile, guard, coaExecutable, tools),
        ];
    }

    /// <summary>The arguments of the process that starts the session.</summary>
    public static IReadOnlyList<string> SpawnArguments(Guid sessionId, IReadOnlyList<string> session, decimal spendCap) =>
        Process("--session-id", sessionId, session, spendCap);

    /// <summary>The arguments of a process that resumes the session.</summary>
    public static IReadOnlyList<string> ResumeArguments(Guid sessionId, IReadOnlyList<string> session, decimal spendCap) =>
        Process("--resume", sessionId, session, spendCap);

    /// <summary>The variable that lists the directories executables are looked up in.</summary>
    public const string PathVariable = "PATH";

    /// <summary>
    /// The environment of every process of a session: the inherited one less every variable an
    /// enclosing Claude Code session set for itself and every variable that would make the CLI skip
    /// hooks, and, when the spawn carries a profile, less the
    /// credential variables too, with the profile's config directory set. With no profile nothing
    /// credential-related is set or removed. An inherited session token, or the home it belongs to, never reaches the session;
    /// the <paramref name="access"/> the spawn carries sets its own token, the node home, and the
    /// project. A <paramref name="coaDirectory"/>, when given, goes first on the PATH, whose name is
    /// matched without regard to case on Windows.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(
        IReadOnlyDictionary<string, string> inherited,
        CredentialProfile? profile,
        SessionAccess? access = null,
        string? coaDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        StringComparer names = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var removed = new HashSet<string>([.. EnclosingSessionVariables, .. HookSkippingVariables, SessionAccess.TokenVariable, SessionAccess.HomeVariable], names);
        if (profile is not null)
        {
            removed.UnionWith(CredentialVariables);
        }

        var environment = new Dictionary<string, string>(names);
        foreach ((string name, string value) in inherited)
        {
            if (!removed.Contains(name))
            {
                environment[name] = value;
            }
        }

        if (profile?.ConfigDirectory is { } directory)
        {
            environment[ConfigDirectoryVariable] = directory;
        }

        foreach ((string name, string value) in access?.Variables ?? new Dictionary<string, string>())
        {
            environment[name] = value;
        }

        if (!string.IsNullOrWhiteSpace(coaDirectory))
        {
            environment[PathVariable] = environment.TryGetValue(PathVariable, out string? path) && path.Length > 0
                ? coaDirectory + Path.PathSeparator + path
                : coaDirectory;
        }

        return environment;
    }

    /// <summary>The persona's five parts, each under its own heading, as the text appended to the CLI's system prompt.</summary>
    public static string Persona(AgentPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        return $"""
            # Role

            {persona.Role}

            # Purpose

            {persona.Purpose}

            # Output

            {persona.Output}

            # Rules

            {persona.Rules}

            # Prohibitions

            {persona.Prohibitions}

            """;
    }

    /// <summary>
    /// The prompt the session's first process receives: the context itself, so an agent that cannot
    /// read files still has it, the names of the two files that hold the task record and the
    /// context, and the answer schema the reply must end in.
    /// </summary>
    public static string Prompt(string context, string answerSchema)
    {
        AnswerSchema schema = AnswerSchemas.Require(answerSchema);
        var prompt = new StringBuilder();
        prompt.Append(context).Append("\n\n");
        prompt.Append($"The task's record is in {SessionFiles.TaskFile} and this context is in {SessionFiles.PromptFile}, both under your working directory.\n\n");
        prompt.Append("End your reply with your answer: exactly one JSON object, and nothing after it, with a \"verdict\" of ")
            .Append(string.Join(" or ", schema.Verdicts.Select(v => $"\"{v}\"")))
            .Append(". The object must be one that the '")
            .Append(schema.Name)
            .Append("' answer schema below accepts; do not repeat the schema itself.\n\n```json\n")
            .Append(schema.Document)
            .Append("\n```\n");
        return prompt.ToString();
    }

    private static List<string> Process(string sessionFlag, Guid sessionId, IReadOnlyList<string> session, decimal spendCap) =>
    [
        "--print",
        "--output-format", "stream-json",
        "--verbose",
        sessionFlag, sessionId.ToString(),
        .. session,
        "--max-budget-usd", spendCap.ToString(CultureInfo.InvariantCulture),
    ];

    private static void RequirePlain(AgentDefinition agent, string part, string value)
    {
        if (!PlainToken().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' names the {part} '{value}', which is not a plain token (letters, digits, dots, hyphens, underscores, and square brackets, not beginning with a hyphen), so no process was started.");
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._\[\]][A-Za-z0-9._\-\[\]]*\z")]
    private static partial Regex PlainToken();
}
