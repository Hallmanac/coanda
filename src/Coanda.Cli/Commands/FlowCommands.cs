using System.ComponentModel;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api.Endpoints;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public sealed class FlowListSettings : ProjectSettings
{
    [CommandOption("--parcel <ID>")]
    [Description("The parcel whose flows to list: its id or the short form coa parcel show accepts.")]
    public required string Parcel { get; init; }
}

public sealed class FlowListCommand : AsyncCommand<FlowListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FlowListSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        ParcelView parcel = await ParcelLookup.FindAsync(client, project, settings.Parcel, cancellationToken);
        List<FlowView> flows = await client.GetAsync<List<FlowView>>(
            $"/flows?project={Uri.EscapeDataString(project)}&parcel={parcel.Id}", cancellationToken);

        if (flows.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"Parcel {Ids.Short(parcel.Id)} has no flows.");
            return 0;
        }

        var table = new Table();
        foreach (string column in FlowRendering.ListColumns)
        {
            table.AddColumn(column);
        }

        foreach (FlowView flow in flows)
        {
            table.AddRow([.. FlowRendering.ListRow(flow).Select(Markup.Escape)]);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class FlowIdSettings : ProjectSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The flow's id or its short form, the last eight characters.")]
    public required string Id { get; init; }
}

public sealed class FlowShowCommand : AsyncCommand<FlowIdSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FlowIdSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        FlowView flow = await FlowLookup.FindAsync(client, project, settings.Id, cancellationToken);
        foreach (string line in FlowRendering.Show(flow))
        {
            AnsiConsole.WriteLine(line);
        }

        return 0;
    }
}

public sealed class ContourStartSettings : ProjectSettings
{
    [CommandArgument(0, "<CONTOUR>")]
    [Description("The published contour to start, by name.")]
    public required string Contour { get; init; }

    [CommandOption("--parcel <ID>")]
    [Description("The parcel to start it on: its id or the short form coa parcel show accepts.")]
    public required string Parcel { get; init; }
}

public sealed class ContourStartCommand : AsyncCommand<ContourStartSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ContourStartSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        ParcelView parcel = await ParcelLookup.FindAsync(client, project, settings.Parcel, cancellationToken);
        FlowView flow = await client.PostAsync<FlowView>(
            "/flows", new FlowStartRequest(project, parcel.Id, settings.Contour), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Started flow {Ids.Short(flow.Id)} of {flow.ContourName} on parcel {Ids.Short(parcel.Id)}.");
        return 0;
    }
}

internal static class FlowLookup
{
    /// <summary>Accepts a full id, the short form (last eight characters), or at least four characters of either end.</summary>
    public static async Task<FlowView> FindAsync(DaemonClient client, string project, string idOrPrefix, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrPrefix, out Guid id))
        {
            return await client.GetAsync<FlowView>($"/flows/{id}?project={Uri.EscapeDataString(project)}", cancellationToken);
        }

        if (idOrPrefix.Length < 4)
        {
            throw new DomainException("Give at least four characters of the flow id.");
        }

        List<FlowView> flows = await client.GetAsync<List<FlowView>>($"/flows?project={Uri.EscapeDataString(project)}", cancellationToken);
        List<FlowView> matches = [.. flows.Where(f => Ids.Matches(f.Id, idOrPrefix))];
        return matches switch
        {
            [var one] => one,
            [] => throw new DomainException($"No flow in '{project}' matches '{idOrPrefix}'."),
            _ => throw new DomainException($"'{idOrPrefix}' matches {matches.Count} flows in '{project}'; give more characters."),
        };
    }
}
