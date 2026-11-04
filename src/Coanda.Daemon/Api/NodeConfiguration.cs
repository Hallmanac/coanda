using System.Text.Json;
using System.Text.Json.Serialization;
using Coanda.Core;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Daemon.Api;

/// <summary>
/// Reads the node configuration, <see cref="NodeHome.ConfigurationPath"/>, into the options the
/// daemon starts with: the executor, named by key, and the credential profiles the node holds per
/// vendor. It is read once, when <c>coa daemon run</c> starts. A missing file is the Claude Code
/// executor running <c>claude</c> from the PATH, with no profiles, so a cold install runs judgments.
/// Anything else the documented format does not allow is refused with a <see cref="DomainException"/>
/// naming the file and the offending entry.
/// </summary>
/// <remarks>
/// The file holds paths and commands only. Nothing here reads a profile's config directory, runs
/// its helper command, or checks that either exists. A relative config directory is resolved
/// against the node home, because a detached start runs in the node home and a foreground one in
/// the shell's directory, and a profile must mean the same thing under both. A relative claude
/// executable path is resolved the same way; a bare name is looked up on the PATH when a judgment spawns.
/// </remarks>
public static class NodeConfiguration
{
    /// <summary>The executor key that runs a scripted executor with no entries.</summary>
    public const string ScriptedExecutor = "scripted";

    /// <summary>The executor key that runs each judgment as a headless Claude Code session.</summary>
    public const string ClaudeCodeExecutor = "claude-code";

    /// <summary>The one vendor a credential profile may be held under.</summary>
    public const string ClaudeCode = "claude-code";

    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    public static DaemonOptions Load(NodeHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        string path = home.ConfigurationPath;
        if (Directory.Exists(path))
        {
            throw Refused(path, "is a directory; the node configuration is a file.");
        }

        if (!File.Exists(path))
        {
            return new DaemonOptions { Executor = ClaudeCodeOf(home, Executors.ClaudeCode.ClaudeCodeExecutor.DefaultExecutable) };
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            throw Refused(path, $"could not be read: {unreadable.Message}");
        }

        FileShape? file;
        try
        {
            file = JsonSerializer.Deserialize<FileShape>(text, Format);
        }
        catch (JsonException malformed)
        {
            throw Refused(path, Malformed(malformed));
        }

        if (file is null || string.IsNullOrWhiteSpace(file.Executor))
        {
            throw Refused(path, $"names no executor; set \"executor\" to \"{ClaudeCodeExecutor}\" or \"{ScriptedExecutor}\".");
        }

        string? claudeExecutable = Given(file.ClaudeExecutable);
        IAgentExecutor executor = (file.Executor, claudeExecutable) switch
        {
            (ClaudeCodeExecutor, _) => ClaudeCodeOf(home, Executable(home, path, claudeExecutable)),
            (ScriptedExecutor, null) => new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>()),
            (ScriptedExecutor, { }) => throw Refused(path, $"gives a \"claudeExecutable\" while the executor is \"{ScriptedExecutor}\"; the path is read only under \"{ClaudeCodeExecutor}\"."),
            _ => throw Refused(path, $"names the executor '{file.Executor}', which is not a key this daemon knows; the keys are \"{ClaudeCodeExecutor}\" and \"{ScriptedExecutor}\"."),
        };

        return new DaemonOptions { Executor = executor, Profiles = Profiles(home, path, file.Profiles) };
    }

    private static ClaudeCodeExecutor ClaudeCodeOf(NodeHome home, string executable) =>
        new(executable, home, Executors.ClaudeCode.ClaudeCodeExecutor.CurrentEnvironment());

    /// <summary>
    /// The claude executable to run: the default name when none is given, a bare name as given (it is
    /// looked up on the PATH when a judgment spawns), and a path resolved against the node home.
    /// </summary>
    private static string Executable(NodeHome home, string path, string? configured)
    {
        if (configured is null)
        {
            return Executors.ClaudeCode.ClaudeCodeExecutor.DefaultExecutable;
        }

        if (configured.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(configured))
        {
            return configured;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(home.Path, configured));
        }
        catch (Exception invalid) when (invalid is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Refused(path, "gives a \"claudeExecutable\" that is not a valid path.");
        }
    }

    private static List<CredentialProfile> Profiles(NodeHome home, string path, Dictionary<string, List<ProfileShape?>?>? vendors)
    {
        List<CredentialProfile> loaded = [];
        foreach ((string vendor, List<ProfileShape?>? entries) in vendors ?? [])
        {
            if (!string.Equals(vendor, ClaudeCode, StringComparison.Ordinal))
            {
                throw Refused(path, $"holds profiles under the vendor '{vendor}'; profiles may be held only under \"{ClaudeCode}\".");
            }

            List<ProfileShape?> held = entries ?? [];
            for (int index = 0; index < held.Count; index++)
            {
                loaded.Add(Profile(home, path, vendor, index, held[index], loaded));
            }
        }

        return loaded;
    }

    private static CredentialProfile Profile(
        NodeHome home, string path, string vendor, int index, ProfileShape? entry, List<CredentialProfile> loaded)
    {
        string position = $"profile {index + 1} under \"{vendor}\"";
        if (entry is null || string.IsNullOrWhiteSpace(entry.Name))
        {
            throw Refused(path, $"has a blank name on {position}; every profile needs a name.");
        }

        string name = entry.Name.Trim();
        if (loaded.Any(p => string.Equals(p.Vendor, vendor, StringComparison.Ordinal) && string.Equals(p.Name, name, StringComparison.Ordinal)))
        {
            throw Refused(path, $"holds more than one profile named '{name}' under \"{vendor}\"; {position} repeats it.");
        }

        return (Given(entry.ConfigDirectory), Given(entry.ApiKeyHelper)) switch
        {
            ({ } directory, null) => new CredentialProfile(vendor, name, Resolved(home, path, vendor, name, directory), null),
            (null, { } helper) => new CredentialProfile(vendor, name, null, helper),
            ({ }, { }) => throw Refused(path, $"gives profile '{name}' under \"{vendor}\" both a \"configDirectory\" and an \"apiKeyHelper\"; a profile holds exactly one."),
            _ => throw Refused(path, $"gives profile '{name}' under \"{vendor}\" neither a \"configDirectory\" nor an \"apiKeyHelper\"; a profile holds exactly one."),
        };
    }

    /// <summary>The config directory resolved against the node home, or a refusal when it is not a path.</summary>
    private static string Resolved(NodeHome home, string path, string vendor, string name, string directory)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(home.Path, directory));
        }
        catch (Exception invalid) when (invalid is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Refused(path, $"gives profile '{name}' under \"{vendor}\" a \"configDirectory\" that is not a valid path.");
        }
    }

    /// <summary>
    /// The rule for a file the serializer refused, written here rather than quoting the serializer,
    /// whose message names this class's private types and repeats the location with a zero-based
    /// line. A syntax error is told apart by the reader exception it wraps; an unknown property, a
    /// repeated one, and a value of the wrong kind all arrive alike, so the rule names all three.
    /// </summary>
    private static string Malformed(JsonException malformed)
    {
        string where = (malformed.Path, malformed.LineNumber) switch
        {
            ({ } at, { } line) => $"at {at} (line {line + 1})",
            ({ } at, null) => $"at {at}",
            (null, { } line) => $"on line {line + 1}",
            _ => "at a position it did not report",
        };

        return malformed.InnerException is JsonException
            ? $"is not valid JSON; the reader stopped {where}."
            : $"holds something the documented format does not allow {where}: a property the format does not name (property names are case-sensitive), a property given twice, or a value of the wrong kind.";
    }

    /// <summary>The value trimmed, or null when it is absent or blank.</summary>
    private static string? Given(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static DomainException Refused(string path, string rule) => new($"The node configuration {path} {rule}");

    private sealed class FileShape
    {
        public string? Executor { get; init; }

        public string? ClaudeExecutable { get; init; }

        public Dictionary<string, List<ProfileShape?>?>? Profiles { get; init; }
    }

    private sealed class ProfileShape
    {
        public string? Name { get; init; }

        public string? ConfigDirectory { get; init; }

        public string? ApiKeyHelper { get; init; }
    }
}
