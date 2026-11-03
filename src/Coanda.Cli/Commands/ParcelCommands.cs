using System.ComponentModel;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public sealed class ParcelAddSettings : ProjectSettings
{
    [CommandOption("-t|--type <TYPE>")]
    [Description("The parcel type: task (default) or idea, or any installed type.")]
    public string Type { get; init; } = "task";

    [CommandOption("--title <TITLE>")]
    [Description("What the parcel is, in a line.")]
    public required string Title { get; init; }

    [CommandOption("--state <STATE>")]
    [Description("An entry state of the type; defaults to the type's first entry state.")]
    public string? State { get; init; }
}

public sealed class ParcelAddCommand : AsyncCommand<ParcelAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ParcelAddSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        var request = new ParcelAddRequest(settings.ResolveProject(), settings.Type, settings.Title, settings.State);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", request, cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Created {parcel.Type} parcel {Ids.Short(parcel.Id)} in {parcel.Project} at {parcel.State}: {parcel.Title}");
        return 0;
    }
}

public sealed class ParcelListSettings : ProjectSettings
{
    [CommandOption("--all")]
    [Description("Include closed parcels.")]
    public bool All { get; init; }
}

public sealed class ParcelListCommand : AsyncCommand<ParcelListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ParcelListSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        List<ParcelView> parcels = await client.GetAsync<List<ParcelView>>($"/parcels?project={Uri.EscapeDataString(project)}", cancellationToken);
        IEnumerable<ParcelView> shown = settings.All ? parcels : parcels.Where(p => p.Resolution is null);

        var table = new Table().AddColumns("Id", "Type", "State", "Title", "Updated");
        foreach (ParcelView parcel in shown)
        {
            string state = parcel.Resolution is null ? parcel.State : $"{parcel.State} ({parcel.Resolution})";
            table.AddRow(
                Ids.Short(parcel.Id),
                parcel.Type,
                state,
                Markup.Escape(parcel.Title),
                parcel.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        }

        if (table.Rows.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"No open parcels in {project}.");
            return 0;
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public class ParcelIdSettings : ProjectSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The parcel's id or its short form, the last eight characters.")]
    public required string Id { get; init; }
}

public sealed class ParcelShowCommand : AsyncCommand<ParcelIdSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ParcelIdSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        ParcelView parcel = await ParcelLookup.FindAsync(client, project, settings.Id, cancellationToken);

        var table = new Table().HideHeaders().AddColumns("k", "v");
        table.AddRow("Id", parcel.Id.ToString());
        table.AddRow("Project", parcel.Project);
        table.AddRow("Type", $"{parcel.Type}@{parcel.TypeHash}");
        table.AddRow("State", parcel.State);
        table.AddRow("Title", Markup.Escape(parcel.Title));
        table.AddRow("Created", $"{parcel.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm zzz} by {parcel.CreatedBy}");
        table.AddRow("Updated", parcel.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz"));
        if (parcel.Resolution is not null)
        {
            table.AddRow("Resolution", $"{parcel.Resolution}: {Markup.Escape(parcel.CloseReason ?? string.Empty)}");
        }

        table.AddRow("Flight", parcel.LiveFlightId is Guid flight ? flight.ToString() : "none");

        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class ParcelMoveSettings : ParcelIdSettings
{
    [CommandArgument(1, "<STATE>")]
    [Description("The state to move to; the parcel's type decides whether the move is allowed.")]
    public required string To { get; init; }
}

public sealed class ParcelMoveCommand : AsyncCommand<ParcelMoveSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ParcelMoveSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        ParcelView found = await ParcelLookup.FindAsync(client, project, settings.Id, cancellationToken);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(project, found.Id, settings.To), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Moved {Ids.Short(parcel.Id)} from {found.State} to {parcel.State}.");
        return 0;
    }
}

public sealed class ParcelCloseSettings : ParcelIdSettings
{
    [CommandOption("-r|--resolution <RESOLUTION>")]
    [Description("How it ended, from the type's resolutions (a task: completed, abandoned, superseded, duplicate).")]
    public required string Resolution { get; init; }

    [CommandOption("--reason <REASON>")]
    [Description("Why, in a sentence.")]
    public required string Reason { get; init; }
}

public sealed class ParcelCloseCommand : AsyncCommand<ParcelCloseSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ParcelCloseSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        ParcelView found = await ParcelLookup.FindAsync(client, project, settings.Id, cancellationToken);
        ParcelView parcel = await client.PostAsync<ParcelView>(
            "/parcels/close", new ParcelCloseRequest(project, found.Id, settings.Resolution, settings.Reason), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Closed {Ids.Short(parcel.Id)} as {parcel.Resolution}.");
        return 0;
    }
}

public sealed class TypeListCommand : AsyncCommand<ProjectSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        List<TypeView> types = await client.GetAsync<List<TypeView>>($"/types?project={Uri.EscapeDataString(project)}", cancellationToken);
        var table = new Table().AddColumns("Type", "Hash", "States", "Resolutions");
        foreach (TypeView type in types)
        {
            table.AddRow(type.Name, type.Hash, string.Join(", ", type.States), string.Join(", ", type.Resolutions));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class FlowListCommand : AsyncCommand<ProjectSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        List<FlowView> flows = await client.GetAsync<List<FlowView>>($"/flows?project={Uri.EscapeDataString(project)}", cancellationToken);
        var table = new Table().AddColumns("Flow", "Version", "Hash");
        foreach (FlowView flow in flows)
        {
            table.AddRow(flow.Name, flow.Version.ToString(), flow.Hash);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

internal static class ParcelLookup
{
    /// <summary>Accepts a full id, the short form (last eight characters), or at least four characters of either end.</summary>
    public static async Task<ParcelView> FindAsync(DaemonClient client, string project, string idOrPrefix, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrPrefix, out Guid id))
        {
            return await client.GetAsync<ParcelView>($"/parcels/{id}?project={Uri.EscapeDataString(project)}", cancellationToken);
        }

        if (idOrPrefix.Length < 4)
        {
            throw new DomainException("Give at least four characters of the parcel id.");
        }

        List<ParcelView> parcels = await client.GetAsync<List<ParcelView>>($"/parcels?project={Uri.EscapeDataString(project)}", cancellationToken);
        List<ParcelView> matches = parcels.Where(p => Ids.Matches(p.Id, idOrPrefix)).ToList();
        return matches switch
        {
            [var one] => one,
            [] => throw new DomainException($"No parcel in '{project}' matches '{idOrPrefix}'."),
            _ => throw new DomainException($"'{idOrPrefix}' matches {matches.Count} parcels in '{project}'; give more characters."),
        };
    }
}
