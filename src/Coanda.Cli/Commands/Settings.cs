using System.ComponentModel;
using Coanda.Daemon.Executors;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public class ProjectSettings : CommandSettings
{
    [CommandOption("-p|--project <NAME>")]
    [Description("The project to work in. Defaults to COANDA_PROJECT, then the current directory's name.")]
    public string? Project { get; init; }

    public string ResolveProject()
    {
        if (!string.IsNullOrWhiteSpace(Project))
        {
            return Project;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(SessionAccess.ProjectVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? new DirectoryInfo(Directory.GetCurrentDirectory()).Name
            : fromEnvironment;
    }
}
