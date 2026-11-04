using System.ComponentModel;
using Coanda.Daemon;
using Coanda.Daemon.Api.Endpoints;
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
    [Description("The remote of the project's repository, recorded exactly as given.")]
    public required string Repo { get; init; }

    [CommandOption("--branch <NAME>")]
    [Description("The repository's primary branch, recorded exactly as given.")]
    public required string Branch { get; init; }

    [CommandOption("--build <COMMAND>")]
    [Description("The command line the gates run first, in the flow's worktree, through the platform's shell. Defaults to dotnet build.")]
    public string? Build { get; init; }

    [CommandOption("--test <COMMAND>")]
    [Description("The command line the gates run once the build passes, in the same way. Defaults to dotnet test.")]
    public string? Test { get; init; }

    /// <summary>The request this registration sends; an option left out is sent as null, so the daemon records the default.</summary>
    public ProjectAddRequest ToRequest() => new(Name, Repo, Branch, Build, Test);
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
        AnsiConsole.MarkupLineInterpolated($"Registered project {project.Name}: {project.Remote} on {project.PrimaryBranch}");
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
        AnsiConsole.MarkupLineInterpolated($"Project {project.Name} spawns its judgments with the credential profile {project.Profile ?? string.Empty} on this node.");
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

    /// <summary>The rows <c>coa project show</c> prints, as markup with the project's own text escaped.</summary>
    public static IReadOnlyList<(string Key, string Value)> ShowRows(ProjectView project) =>
    [
        ("Name", Markup.Escape(project.Name)),
        ("Remote", Markup.Escape(project.Remote)),
        ("Branch", Markup.Escape(project.PrimaryBranch)),
        ("Build", Markup.Escape(project.Build)),
        ("Test", Markup.Escape(project.Test)),
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

        int nameWidth = projects.Max(p => p.Name.Length);
        int remoteWidth = projects.Max(p => p.Remote.Length);
        foreach (ProjectView project in projects)
        {
            AnsiConsole.MarkupLineInterpolated($"{project.Name.PadRight(nameWidth)}  {project.Remote.PadRight(remoteWidth)}  {project.PrimaryBranch}");
        }

        return 0;
    }
}
