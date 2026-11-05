using System.ComponentModel;
using System.Text.Json;
using Coanda.Core.Projects;
using Coanda.Daemon;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Modules;
using Coanda.Modules.Software;
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

    [CommandOption("--branch <NAME>")]
    [Description("The repo's primary branch, recorded as the software module's primary-branch setting for the project.")]
    public string? Branch { get; init; }

    [CommandOption("--build <COMMAND>")]
    [Description("The command line the gates run first, in the flow run's worktree, through the platform's shell, recorded as the software module's build setting. Defaults to dotnet build.")]
    public string? Build { get; init; }

    [CommandOption("--test <COMMAND>")]
    [Description("The command line the gates run once the build passes, in the same way, recorded as the software module's test setting. Defaults to dotnet test.")]
    public string? Test { get; init; }

    public override ValidationResult Validate() =>
        (Repo, Branch) switch
        {
            (var repo, _) when string.IsNullOrWhiteSpace(repo) => ValidationResult.Error("Name the remote of the project's repo with --repo <url>."),
            (_, var branch) when string.IsNullOrWhiteSpace(branch) => ValidationResult.Error("Name the repo's primary branch with --branch <name>."),
            _ => ValidationResult.Success(),
        };

    /// <summary>
    /// The request this registration sends: the project and its repo's remote, and as the software
    /// module's settings, the repo's primary branch and the build and test commands given. An option
    /// left out is not sent, so the module's default stands.
    /// </summary>
    public ProjectAddRequest ToRequest()
    {
        string remote = Repo ?? string.Empty;
        List<ModuleSettingValue> settings =
        [
            Setting(SoftwareSettings.PrimaryBranch, new Dictionary<string, string?> { [ProjectRepo.NameOf(remote)] = Branch }),
            .. Build is null ? Array.Empty<ModuleSettingValue>() : [Setting(SoftwareSettings.Build, Build)],
            .. Test is null ? Array.Empty<ModuleSettingValue>() : [Setting(SoftwareSettings.Test, Test)],
        ];
        return new ProjectAddRequest(Name, remote, settings);
    }

    private static ModuleSettingValue Setting(string name, object value) =>
        new(SoftwareSteps.Module, name, JsonSerializer.SerializeToElement(value));
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
        AnsiConsole.MarkupLineInterpolated(
            $"Registered project {project.Name} with repo {string.Join(", ", project.Repos.Select(r => $"{r.Name} ({r.Remote})"))}; coa project module show {project.Name} {SoftwareSteps.Module} shows the software module's settings for it.");
        return 0;
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
