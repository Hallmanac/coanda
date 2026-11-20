using System.ComponentModel;
using Coanda.Daemon;
using Coanda.Daemon.Api.Endpoints;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

/// <summary>
/// The settings of <c>coa attention list</c>. Its project option has no default: with none given it lists
/// every project the node holds, rather than narrowing to <c>COANDA_PROJECT</c> or the current directory's
/// name as <see cref="ProjectSettings"/> does.
/// </summary>
public sealed class AttentionListSettings : CommandSettings
{
    [CommandOption("-p|--project <NAME>")]
    [Description("The one project to list. With none given, every project the node holds is listed.")]
    public string? Project { get; init; }
}

public sealed class AttentionListCommand : AsyncCommand<AttentionListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AttentionListSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        List<AttentionView> paused = await client.GetAsync<List<AttentionView>>(AttentionRendering.PathFor(settings.Project), cancellationToken);

        if (paused.Count == 0)
        {
            AnsiConsole.WriteLine(AttentionRendering.Nothing(settings.Project));
            return 0;
        }

        // The ids, names, and the time keep to one line each, so a narrow terminal wraps only the free text.
        var table = new Table().Border(TableBorder.Simple);
        foreach (string column in AttentionRendering.Columns)
        {
            var added = new TableColumn(column);
            table.AddColumn(AttentionRendering.Wraps(column) ? added : added.NoWrap());
        }

        foreach (AttentionView entry in paused)
        {
            table.AddRow([.. AttentionRendering.Row(entry).Select(Markup.Escape)]);
        }

        // Written to a terminal, the table fits its width. Written anywhere else, there is no width to fit, so
        // no row is wrapped at the 80 columns the console would otherwise assume.
        if (Console.IsOutputRedirected)
        {
            AnsiConsole.Profile.Width = Math.Max(AnsiConsole.Profile.Width, AttentionRendering.UnwrappedWidth);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
