using System.ComponentModel;
using System.Text.Json;
using Coanda.Daemon;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Modules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public class ProjectNameSettings : CommandSettings
{
    [CommandArgument(0, "<NAME>")]
    [Description("The project's name, which is also the name every other command's --project gives.")]
    public required string Name { get; init; }
}

public sealed class ProjectAddSettings : ProjectNameSettings
{
    [CommandOption("--repo <URL>")]
    [Description("The remote of the project's first repo, recorded exactly as given. The repo is named after the remote's last path segment.")]
    public string? Repo { get; init; }

    [CommandOption("--setting <MODULE/NAME=VALUE>")]
    [Description("One of a switched-on module's replicated settings, recorded with the registration: the module's name, a slash, the setting's name as the module declares it, an equals sign, and the value, read as JSON when it is JSON and as text otherwise. Repeat it for each setting.")]
    public string[]? Settings { get; init; }

    public override ValidationResult Validate() =>
        (Repo, (Settings ?? []).FirstOrDefault(s => ModuleSettingOption.Parse(s) is null)) switch
        {
            (var repo, _) when string.IsNullOrWhiteSpace(repo) => ValidationResult.Error("Name the remote of the project's repo with --repo <url>."),
            (_, { } malformed) => ValidationResult.Error($"Give each setting as --setting <module>/<name>=<value>; '{malformed}' is not in that form."),
            _ => ValidationResult.Success(),
        };

    /// <summary>
    /// The request this registration sends: the project, its repo's remote, and each <c>--setting</c> as the
    /// named module's setting. The daemon decides each against the settings the module's describe answer
    /// declares, so a setting left out keeps the module's default.
    /// </summary>
    public ProjectAddRequest ToRequest() =>
        new(Name, Repo ?? string.Empty, [.. (Settings ?? []).Select(s => ModuleSettingOption.Parse(s)
            ?? throw new InvalidOperationException($"The setting '{s}' was not checked before the request was built."))]);
}

/// <summary>
/// How a command reads a module setting given as text: <c>&lt;module&gt;/&lt;name&gt;=&lt;value&gt;</c>, split at
/// the first <c>=</c> and the module and name at the first <c>/</c>, with the value read by <see cref="ValueOf"/>.
/// </summary>
public static class ModuleSettingOption
{
    /// <summary>The setting <paramref name="option"/> gives, or null when it has no module, no setting name, or no <c>=</c>.</summary>
    public static ModuleSettingValue? Parse(string? option)
    {
        int equals = option?.IndexOf('=', StringComparison.Ordinal) ?? -1;
        if (option is null || equals < 0)
        {
            return null;
        }

        string[] named = option[..equals].Split('/', 2);
        return named is [string module, string name] && !string.IsNullOrWhiteSpace(module) && !string.IsNullOrWhiteSpace(name)
            ? new ModuleSettingValue(module, name, ValueOf(option[(equals + 1)..]))
            : null;
    }

    /// <summary>The value as JSON when it parses as JSON, and as a JSON string holding it otherwise.</summary>
    public static JsonElement ValueOf(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(value);
        }
    }
}

public sealed class ProjectSetProfileSettings : ProjectNameSettings
{
    [CommandArgument(1, "<PROFILE>")]
    [Description("The name of a credential profile the running daemon's node configuration holds.")]
    public required string Profile { get; init; }
}

public sealed class ProjectAddCommand : AsyncCommand<ProjectAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectAddSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        ProjectView project = await client.PostAsync<ProjectView>(
            "/projects", settings.ToRequest(), cancellationToken);
        AnsiConsole.WriteLine(RegisteredLine(project));
        return 0;
    }

    /// <summary>
    /// The line a registration prints: the project and its repos, then each module its record switches on
    /// with the <c>coa project module show</c> command that shows that module's settings, or that it
    /// switches on none.
    /// </summary>
    public static string RegisteredLine(ProjectView project)
    {
        string registered = $"Registered project {project.Name} with repo {string.Join(", ", project.Repos.Select(r => $"{r.Name} ({r.Remote})"))}";
        return project.Modules.Count == 0
            ? $"{registered}; no module is switched on for it."
            : $"{registered}; switched on for it: {string.Join("; ", project.Modules.Select(m => $"module {m}, whose settings coa project module show {project.Name} {m} shows"))}.";
    }
}

public sealed class ProjectSetProfileCommand : AsyncCommand<ProjectSetProfileSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectSetProfileSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        ProjectView project = await client.PostAsync<ProjectView>(
            "/projects/profile", new ProjectSetProfileRequest(settings.Name, settings.Profile), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Project {project.Name} spawns its agent steps with the credential profile {project.Profile ?? string.Empty} on this node.");
        return 0;
    }
}

public sealed class ProjectShowCommand : AsyncCommand<ProjectNameSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectNameSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        ProjectView project = await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf(settings.Name), cancellationToken);

        var table = new Table().HideHeaders().AddColumns("k", "v");
        foreach ((string key, string value) in ShowRows(project))
        {
            table.AddRow(key, value);
        }

        AnsiConsole.Write(table);
        return 0;
    }

    /// <summary>
    /// The rows <c>coa project show</c> prints, as markup with the project's own text escaped: its name,
    /// each repo's name and remote, the agent-step profile it names, and its registration. A module's
    /// settings for it are <c>coa project module show</c>'s.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> ShowRows(ProjectView project) =>
    [
        ("Name", Markup.Escape(project.Name)),
        .. project.Repos.Select(r => ("Repo", $"{Markup.Escape(r.Name)}  {Markup.Escape(r.Remote)}")),
        ("Profile", project.Profile is null
            ? "none set; coa project set-profile names one"
            : Markup.Escape(project.Profile)),
        ("Registered", $"{project.RegisteredAt.ToLocalTime():yyyy-MM-dd HH:mm zzz} by {Markup.Escape(project.RegisteredBy)}"),
    ];
}

public sealed class ProjectListCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        List<ProjectView> projects = await client.GetAsync<List<ProjectView>>("/projects", cancellationToken);
        if (projects.Count == 0)
        {
            AnsiConsole.MarkupLine("No projects are registered on this node; coa project add registers one.");
            return 0;
        }

        foreach (string line in ListLines(projects))
        {
            AnsiConsole.WriteLine(line);
        }

        return 0;
    }

    /// <summary>
    /// The lines <c>coa project list</c> prints: each project's name, then for each of its repos the
    /// repo's name and remote, in aligned columns, a project's later repos on lines of their own.
    /// </summary>
    public static IReadOnlyList<string> ListLines(IReadOnlyList<ProjectView> projects)
    {
        int nameWidth = projects.Max(p => p.Name.Length);
        int repoWidth = projects.SelectMany(p => p.Repos).Select(r => r.Name.Length).DefaultIfEmpty(0).Max();
        List<string> lines = [];
        foreach (ProjectView project in projects)
        {
            string name = project.Name;
            foreach (ProjectRepoView repo in project.Repos)
            {
                lines.Add($"{name.PadRight(nameWidth)}  {repo.Name.PadRight(repoWidth)}  {repo.Remote}");
                name = string.Empty;
            }
        }

        return lines;
    }
}
