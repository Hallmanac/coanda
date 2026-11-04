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
            "/projects", new ProjectAddRequest(settings.Name, settings.Repo, settings.Branch), cancellationToken);
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
        table.AddRow("Name", Markup.Escape(project.Name));
        table.AddRow("Remote", Markup.Escape(project.Remote));
        table.AddRow("Branch", Markup.Escape(project.PrimaryBranch));
        table.AddRow("Profile", project.Profile is null
            ? "none set; coa project set-profile names one"
            : Markup.Escape(project.Profile));
        table.AddRow("Registered", $"{project.RegisteredAt.ToLocalTime():yyyy-MM-dd HH:mm zzz} by {Markup.Escape(project.RegisteredBy)}");

        AnsiConsole.Write(table);
        return 0;
    }
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
