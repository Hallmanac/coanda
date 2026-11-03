using Coanda.Daemon;
using Coanda.Daemon.Api;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public sealed class AgentListCommand : AsyncCommand<ProjectSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        List<AgentView> agents = await client.GetAsync<List<AgentView>>($"/agents?project={Uri.EscapeDataString(project)}", cancellationToken);
        var table = new Table().AddColumn("Agent").AddColumn("Version").AddColumn(new TableColumn("Model") { NoWrap = true }).AddColumn("Hash");
        foreach (AgentView agent in agents)
        {
            table.AddRow(
                Markup.Escape(agent.Name),
                agent.Version.ToString(),
                Markup.Escape(agent.Model.ToString()),
                agent.Hash);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
