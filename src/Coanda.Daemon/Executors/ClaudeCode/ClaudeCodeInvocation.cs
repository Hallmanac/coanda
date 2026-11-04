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
/// operator's own settings name. <c>--setting-sources ""</c> loads no user, project, or local
/// settings (and with them no hooks and no CLAUDE.md), <c>--strict-mcp-config</c> with no
/// <c>--mcp-config</c> loads no MCP server, and the CLI still turns on its automatic memory under
/// the operator's config directory unless the settings document says <c>autoMemoryEnabled</c> is false.
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

    /// <summary>The settings document every process of a session receives through <c>--settings</c>.</summary>
    public static string Settings(CredentialProfile? profile)
    {
        var settings = new JsonObject { [AutoMemorySetting] = false };
        if (profile?.ApiKeyHelper is { } helper)
        {
            settings[ApiKeyHelperSetting] = helper;
        }

        return settings.ToJsonString();
    }

    /// <summary>
    /// The arguments every process of a session carries, spawn and resume alike, since the CLI keeps
    /// none of them with the session: the persona file, the model and effort, the tools and their
    /// pre-approval, the permission mode, no MCP servers, no setting sources, and the settings document.
    /// </summary>
    public static IReadOnlyList<string> SessionArguments(AgentDefinition agent, CredentialProfile? profile)
    {
        IReadOnlyList<string> tools = ToolsOf(agent);
        string joined = string.Join(',', tools);
        List<string> arguments =
        [
            "--append-system-prompt-file", SessionFiles.PersonaFile,
            "--model", agent.Model.Model,
            "--effort", agent.Model.Effort,
            "--tools", joined,
        ];
        if (tools.Count > 0)
        {
            arguments.AddRange(["--allowedTools", joined]);
        }

        arguments.AddRange(
        [
            "--permission-mode", PermissionMode,
            "--permission-prompts", PermissionPrompts,
            "--strict-mcp-config",
            "--setting-sources", string.Empty,
            "--settings", Settings(profile),
        ]);
        return arguments;
    }

    /// <summary>The arguments of the process that starts the session.</summary>
    public static IReadOnlyList<string> SpawnArguments(Guid sessionId, IReadOnlyList<string> session, decimal spendCap) =>
        Process("--session-id", sessionId, session, spendCap);

    /// <summary>The arguments of a process that resumes the session.</summary>
    public static IReadOnlyList<string> ResumeArguments(Guid sessionId, IReadOnlyList<string> session, decimal spendCap) =>
        Process("--resume", sessionId, session, spendCap);

    /// <summary>
    /// The environment of every process of a session: the inherited one less every variable an
    /// enclosing Claude Code session set for itself, and, when the spawn carries a profile, less the
    /// credential variables too, with the profile's config directory set. With no profile nothing
    /// credential-related is set or removed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(IReadOnlyDictionary<string, string> inherited, CredentialProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        StringComparer names = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var removed = new HashSet<string>(EnclosingSessionVariables, names);
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
