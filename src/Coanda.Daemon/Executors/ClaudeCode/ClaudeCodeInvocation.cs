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
/// <para>
/// The inbox behaviours below are confirmed against Claude Code 2.1.329, not 2.1.318, which could not
/// be run (<see cref="ClaudeInbox"/> says how a message is posted into an inbox). Every session that
/// is not in bare mode, print mode included, binds an inbox other local sessions can post messages
/// into. The settings document sets <c>crossSessionInbound</c> to <c>refuse</c>, so a session drops
/// every message posted into its inbox without delivering it; managed settings outrank the value the
/// document sets, so an operator's managed policy can let those messages in again. The CLI exports
/// its own inbox's socket path and token to its hooks and Bash commands
/// (<see cref="InboxVariables"/>), whatever the environment it was started with, and a session's
/// shell could otherwise find another session's inbox beside its own and post into it, which needs
/// no token on macOS or Linux. So every process of a session is started with
/// <see cref="EnvironmentFileVariable"/> naming a script that removes those variables before each of
/// its Bash commands (<see cref="InboxVariablesScript"/>), and its tool guard refuses a path in a
/// directory the CLI binds inboxes in (<see cref="InboxDirectories"/>). A command that builds such a
/// path at run time, which the guard cannot see, still reaches it; a confined session has no shell.
/// </para>
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

    /// <summary>The setting that says what a session does with a message another local session posts into its inbox.</summary>
    public const string CrossSessionInboundSetting = "crossSessionInbound";

    /// <summary>The <see cref="CrossSessionInboundSetting"/> value that drops every such message without delivering it, which the settings document sets.</summary>
    public const string RefuseInbound = "refuse";

    /// <summary>The variable naming a script the CLI sources before each Bash command a session runs.</summary>
    public const string EnvironmentFileVariable = "CLAUDE_ENV_FILE";

    /// <summary>The name, under the node home's sessions directory, of the script <see cref="EnvironmentFileVariable"/> names for every session.</summary>
    public const string InboxVariablesScriptName = "inbox-variables.sh";

    /// <summary>The variables the CLI exports to a session's hooks and Bash commands naming the session's own inbox: its socket path and its token.</summary>
    public static IReadOnlyList<string> InboxVariables { get; } = ["CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN"];

    /// <summary>The script every session's Bash commands are preceded by: it removes each of <see cref="InboxVariables"/>.</summary>
    public static string InboxVariablesScript => $"unset {string.Join(' ', InboxVariables)}\n";

    /// <summary>The name of the directory Claude Code binds sessions' inboxes in, under the run-time or temporary directory it picks.</summary>
    public const string InboxDirectoryName = "cc-socks";

    /// <summary>The setting whose <c>allow</c> rules pre-approve the session's tools.</summary>
    public const string PermissionsSetting = "permissions";

    /// <summary>The pre-tool-use matcher that matches a call to every tool, which a confined session's guard is registered with.</summary>
    public const string AllToolsMatcher = "*";

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

    /// <summary>
    /// The variables a confined session's environment may carry from the one the daemon inherited;
    /// every other inherited variable is left out. The spawn still sets the profile's config
    /// directory and <see cref="EnvironmentFileVariable"/>, and puts the <c>coa</c> directory first on the PATH, and the credential variables
    /// are removed when the spawn carries a profile, as for any session. Names are matched without
    /// regard to case on Windows.
    /// </summary>
    /// <remarks>
    /// The proxy and certificate variables are carried because the CLI's own calls to its API go
    /// through them, and a node that needs them reaches the API no other way; a confined session has
    /// no shell or web tool, so nothing else in it can use them, and a profile's API key helper runs
    /// with this environment too. Variables that choose another API provider or endpoint
    /// (<c>ANTHROPIC_BASE_URL</c>, <c>CLAUDE_CODE_USE_BEDROCK</c>, and the like) are not carried.
    /// </remarks>
    public static IReadOnlyList<string> ConfinedEnvironmentVariables { get; } =
    [
        // Where the CLI and the hooks' coa are looked up, and on Windows how a bare name becomes a file.
        PathVariable,
        "PATHEXT",

        // The home directory, which the CLI reads its default config and login from and the guard expands ~ to.
        "HOME",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",

        // The user's name, which the CLI's login lookup in the system keychain reads.
        "USER",
        "LOGNAME",
        "USERNAME",

        // The temporary directory the CLI and the .NET runtime write scratch files to.
        "TMPDIR",
        "TEMP",
        "TMP",

        // The locale, so the CLI and coa read and write text as the daemon's user does.
        "LANG",
        "LANGUAGE",
        "LC_ALL",
        "LC_CTYPE",
        "LC_MESSAGES",

        // The config directory the CLI uses when the spawn carries no profile, which the guard's rules name.
        ConfigDirectoryVariable,

        // The CLI's credentials when the spawn carries no profile; removed with a profile, as for any session.
        .. CredentialVariables,

        // The network settings the CLI's own calls to its API need behind a proxy or a private certificate authority.
        "HTTPS_PROXY",
        "https_proxy",
        "HTTP_PROXY",
        "http_proxy",
        "NO_PROXY",
        "no_proxy",
        "NODE_EXTRA_CA_CERTS",

        // Where a framework-dependent .NET apphost, which coa is, finds its runtime.
        "DOTNET_ROOT",
        "DOTNET_ROOT_X64",
        "DOTNET_ROOT_ARM64",
        "DOTNET_ROOT_X86",
        "DOTNET_ROOT(x86)",

        // The Windows system variables a process needs to load system libraries and find its own folders.
        "SystemRoot",
        "SystemDrive",
        "windir",
        "ComSpec",
        "APPDATA",
        "LOCALAPPDATA",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "PROCESSOR_ARCHITECTURE",
        "NUMBER_OF_PROCESSORS",
        "OS",

        // The Git Bash the CLI requires on Windows before it starts, when it is not found on the PATH.
        "CLAUDE_CODE_GIT_BASH_PATH",
    ];

    /// <summary>
    /// The Claude Code tools an agent's catalog names map to, in catalog order, each once. A confined
    /// agent gets only those of them in <see cref="ToolGuard.ConfinedTools"/>, whatever its catalog
    /// holds, so it never gets a shell or a web tool.
    /// </summary>
    public static IReadOnlyList<string> ToolsOf(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return [.. ToolCatalog.Names
            .Where(agent.HoldsTool)
            .SelectMany(name => Tools[name])
            .Where(tool => !agent.Confined || ToolGuard.ConfinedTools.Contains(tool, StringComparer.Ordinal))];
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
    /// memory off, no attribution on commits or pull requests, every message another local session
    /// posts into its inbox refused, a helper profile's API key helper,
    /// permission rules that pre-approve each of <paramref name="tools"/> (none when it is empty),
    /// hooks left on, the <see cref="SettingsEvidence"/> hook registered for every session start, and
    /// the tool guard registered before every call to the six tools it judges, or before every call
    /// to any tool when <paramref name="guard"/> is a confined session's. Both hooks run as
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
            [CrossSessionInboundSetting] = RefuseInbound,
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
                ["matcher"] = guard.Confined
                    ? AllToolsMatcher
                    : string.Join('|', ToolGuard.GuardedTools),
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
    /// <paramref name="profileDirectories"/>, the config directories of the node's profiles, and every
    /// directory the CLI may bind sessions' inboxes in (<see cref="InboxDirectories"/>). With
    /// <paramref name="confined"/>, they are a confined session's rules.
    /// </summary>
    public static ToolGuardRules Guard(
        NodeHome home,
        string workingDirectory,
        CredentialProfile? profile,
        IReadOnlyDictionary<string, string> inherited,
        IEnumerable<string> profileDirectories,
        bool confined = false)
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
        return new ToolGuardRules(Path.GetFullPath(home.Path), work, session, others, confined, InboxDirectories(inherited));
    }

    /// <summary>
    /// The directories the CLI binds a session's inbox socket in on macOS and Linux, wherever a session
    /// started with <paramref name="inherited"/> or with none of its variables would bind it:
    /// <see cref="InboxDirectoryName"/> under <c>XDG_RUNTIME_DIR</c> when it is set, under
    /// <c>CLAUDE_CODE_TMPDIR</c> when it is set, and under <c>/tmp</c>, and <c>/tmp/cc-socks-</c> followed
    /// by the daemon's user id, where the CLI binds when the first would make too long a socket path.
    /// None on Windows, where an inbox is a named pipe and not a file.
    /// </summary>
    public static IReadOnlyList<string> InboxDirectories(IReadOnlyDictionary<string, string> inherited)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        if (OperatingSystem.IsWindows())
        {
            return [];
        }

        string fallback = $"/tmp/{InboxDirectoryName}-{DetachedProcess.Posix.UserId().ToString(CultureInfo.InvariantCulture)}";
        return [.. new[] { "XDG_RUNTIME_DIR", "CLAUDE_CODE_TMPDIR" }
            .Select(name => inherited.GetValueOrDefault(name))
            .OfType<string>()
            .Where(directory => directory.Trim().Length > 0 && Path.IsPathFullyQualified(directory))
            .Select(directory => Path.Combine(directory, InboxDirectoryName))
            .Append(Path.Combine("/tmp", InboxDirectoryName))
            .Append(fallback)
            .Select(directory => Path.GetFullPath(directory))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The arguments every process of a session carries, spawn and resume alike, since the CLI keeps
    /// none of them with the session: the persona file, the model and effort, the tools, the
    /// permission mode, no MCP servers, no setting sources, and the settings document, which carries
    /// the tools' pre-approval, the evidence that it was applied, and the session's tool guard, which
    /// is a confined session's exactly when the agent declares itself confined.
    /// </summary>
    public static IReadOnlyList<string> SessionArguments(AgentDefinition agent, CredentialProfile? profile, ToolGuardRules guard, string coaExecutable)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(guard);
        if (agent.Confined != guard.Confined)
        {
            throw new ArgumentException(
                $"Agent '{agent.Name}' is {(agent.Confined ? "confined" : "not confined")} and its tool guard's rules are {(guard.Confined ? "confined" : "not confined")}, so no process was started.",
                nameof(guard));
        }

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
    /// matched without regard to case on Windows. A <paramref name="confined"/> session's environment
    /// starts from only the inherited variables <see cref="ConfinedEnvironmentVariables"/> names, and
    /// carries no <paramref name="access"/>: it has no shell to run <c>coa</c> in, and both hooks read
    /// only their arguments. An inherited <see cref="EnvironmentFileVariable"/> never reaches a
    /// session; an <paramref name="environmentFile"/>, when given, is set in its place, and is the
    /// script holding <see cref="InboxVariablesScript"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(
        IReadOnlyDictionary<string, string> inherited,
        CredentialProfile? profile,
        SessionAccess? access = null,
        string? coaDirectory = null,
        bool confined = false,
        string? environmentFile = null)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        StringComparer names = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        HashSet<string>? allowed = confined ? new HashSet<string>(ConfinedEnvironmentVariables, names) : null;
        var removed = new HashSet<string>([.. EnclosingSessionVariables, .. HookSkippingVariables, EnvironmentFileVariable, SessionAccess.TokenVariable, SessionAccess.HomeVariable], names);
        if (profile is not null)
        {
            removed.UnionWith(CredentialVariables);
        }

        var environment = new Dictionary<string, string>(names);
        foreach ((string name, string value) in inherited)
        {
            if (!removed.Contains(name) && allowed?.Contains(name) != false)
            {
                environment[name] = value;
            }
        }

        if (profile?.ConfigDirectory is { } directory)
        {
            environment[ConfigDirectoryVariable] = directory;
        }

        foreach ((string name, string value) in (confined ? null : access)?.Variables ?? new Dictionary<string, string>())
        {
            environment[name] = value;
        }

        if (!string.IsNullOrWhiteSpace(environmentFile))
        {
            environment[EnvironmentFileVariable] = environmentFile;
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
    /// read files still has it, the names of the two files that hold the parcel's record and the
    /// context, and the answer schema the reply must end in.
    /// </summary>
    public static string Prompt(string context, AnswerSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var prompt = new StringBuilder();
        prompt.Append(context).Append("\n\n");
        prompt.Append($"The parcel's record is in {SessionFiles.ParcelFile} and this context is in {SessionFiles.PromptFile}, both under your working directory.\n\n");
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
