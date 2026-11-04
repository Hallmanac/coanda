using System.ComponentModel;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api.Endpoints;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public sealed class FlowRunListSettings : ProjectSettings
{
    [CommandOption("--parcel <ID>")]
    [Description("The parcel whose flow runs to list: its id or the short form coa parcel show accepts.")]
    public required string Parcel { get; init; }
}

public sealed class FlowRunListCommand : AsyncCommand<FlowRunListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FlowRunListSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        ParcelView parcel = await ParcelLookup.FindAsync(client, project, settings.Parcel, cancellationToken);
        List<FlowRunView> flowRuns = await client.GetAsync<List<FlowRunView>>(
            $"/flow-runs?project={Uri.EscapeDataString(project)}&parcel={parcel.Id}", cancellationToken);

        if (flowRuns.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"Parcel {Ids.Short(parcel.Id)} has no flow runs.");
            return 0;
        }

        var table = new Table();
        foreach (string column in FlowRunRendering.ListColumns)
        {
            table.AddColumn(column);
        }

        foreach (FlowRunView flowRun in flowRuns)
        {
            table.AddRow([.. FlowRunRendering.ListRow(flowRun).Select(Markup.Escape)]);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public class FlowRunIdSettings : ProjectSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The flow run's id or its short form, the last eight characters.")]
    public required string Id { get; init; }
}

public sealed class FlowRunShowCommand : AsyncCommand<FlowRunIdSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FlowRunIdSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        FlowRunView flowRun = await FlowRunLookup.FindAsync(client, project, settings.Id, cancellationToken);
        foreach (string line in FlowRunRendering.Show(flowRun))
        {
            AnsiConsole.WriteLine(line);
        }

        return 0;
    }
}

public sealed class FlowRunPauseSettings : FlowRunIdSettings
{
    [CommandOption("--reason <REASON>")]
    [Description("Why the flow run pauses, in a sentence.")]
    public string? Reason { get; init; }
}

public sealed class FlowRunPauseCommand : AsyncCommand<FlowRunPauseSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FlowRunPauseSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        FlowRunView found = await FlowRunLookup.FindAsync(client, project, settings.Id, cancellationToken);

        // A missing reason goes to the daemon as an empty one, so the service decides whether the flow run can pause before asking for it.
        FlowRunView flowRun = await client.PostAsync<FlowRunView>(
            $"/flow-runs/{found.Id}/pause", new FlowRunPauseRequest(project, settings.Reason ?? string.Empty), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Flow run {Ids.Short(flowRun.Id)} is {flowRun.Status} at {flowRun.CurrentStep}; resume it to perform that step again.");
        return 0;
    }
}

public sealed class FlowRunStopSettings : FlowRunIdSettings
{
    [CommandOption("--reason <REASON>")]
    [Description("Why the flow run stops, in a sentence.")]
    public string? Reason { get; init; }
}

public sealed class FlowRunStopCommand : AsyncCommand<FlowRunStopSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FlowRunStopSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        FlowRunView found = await FlowRunLookup.FindAsync(client, project, settings.Id, cancellationToken);
        FlowRunView flowRun = await client.PostAsync<FlowRunView>(
            $"/flow-runs/{found.Id}/stop", new FlowRunStopRequest(project, settings.Reason ?? string.Empty), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Flow run {Ids.Short(flowRun.Id)} is {flowRun.Status}; parcel {Ids.Short(flowRun.ParcelId)} is free to move or close.");
        return 0;
    }
}

public sealed class FlowRunResumeSettings : FlowRunIdSettings
{
    [CommandOption("--note <NOTE>")]
    [Description("What changed since the pause, for the flow run's record.")]
    public string? Note { get; init; }
}

public sealed class FlowRunResumeCommand : AsyncCommand<FlowRunResumeSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FlowRunResumeSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        FlowRunView found = await FlowRunLookup.FindAsync(client, project, settings.Id, cancellationToken);
        FlowRunView flowRun = await client.PostAsync<FlowRunView>(
            $"/flow-runs/{found.Id}/resume", new FlowRunResumeRequest(project, settings.Note), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Flow run {Ids.Short(flowRun.Id)} is {flowRun.Status} at {flowRun.CurrentStep}; the daemon advances it from there.");
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
        FlowRunView flowRun = await client.PostAsync<FlowRunView>(
            "/flow-runs", new FlowRunStartRequest(project, parcel.Id, settings.Contour), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Started flow run {Ids.Short(flowRun.Id)} of {flowRun.ContourName} on parcel {Ids.Short(parcel.Id)}.");
        return 0;
    }
}

internal static class FlowRunLookup
{
    /// <summary>Accepts a full id, the short form (last eight characters), or at least four characters of either end.</summary>
    public static async Task<FlowRunView> FindAsync(DaemonClient client, string project, string idOrPrefix, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrPrefix, out Guid id))
        {
            return await client.GetAsync<FlowRunView>($"/flow-runs/{id}?project={Uri.EscapeDataString(project)}", cancellationToken);
        }

        if (idOrPrefix.Length < 4)
        {
            throw new DomainException("Give at least four characters of the flow run id.");
        }

        List<FlowRunView> flowRuns = await client.GetAsync<List<FlowRunView>>($"/flow-runs?project={Uri.EscapeDataString(project)}", cancellationToken);
        List<FlowRunView> matches = [.. flowRuns.Where(f => Ids.Matches(f.Id, idOrPrefix))];
        return matches switch
        {
            [var one] => one,
            [] => throw new DomainException($"No flow run in '{project}' matches '{idOrPrefix}'."),
            _ => throw new DomainException($"'{idOrPrefix}' matches {matches.Count} flow runs in '{project}'; give more characters."),
        };
    }
}
