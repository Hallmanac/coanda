using System.ComponentModel;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public sealed class FlightListSettings : ProjectSettings
{
    [CommandOption("--parcel <ID>")]
    [Description("The parcel whose flights to list: its id or the short form coa parcel show accepts.")]
    public required string Parcel { get; init; }
}

public sealed class FlightListCommand : AsyncCommand<FlightListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FlightListSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        ParcelView parcel = await ParcelLookup.FindAsync(client, project, settings.Parcel, cancellationToken);
        List<FlightView> flights = await client.GetAsync<List<FlightView>>(
            $"/flights?project={Uri.EscapeDataString(project)}&parcel={parcel.Id}", cancellationToken);

        if (flights.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"Parcel {Ids.Short(parcel.Id)} has no flights.");
            return 0;
        }

        var table = new Table();
        foreach (string column in FlightRendering.ListColumns)
        {
            table.AddColumn(column);
        }

        foreach (FlightView flight in flights)
        {
            table.AddRow([.. FlightRendering.ListRow(flight).Select(Markup.Escape)]);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class FlightIdSettings : ProjectSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The flight's id or its short form, the last eight characters.")]
    public required string Id { get; init; }
}

public sealed class FlightShowCommand : AsyncCommand<FlightIdSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FlightIdSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        FlightView flight = await FlightLookup.FindAsync(client, project, settings.Id, cancellationToken);
        foreach (string line in FlightRendering.Show(flight))
        {
            AnsiConsole.WriteLine(line);
        }

        return 0;
    }
}

internal static class FlightLookup
{
    /// <summary>Accepts a full id, the short form (last eight characters), or at least four characters of either end.</summary>
    public static async Task<FlightView> FindAsync(DaemonClient client, string project, string idOrPrefix, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrPrefix, out Guid id))
        {
            return await client.GetAsync<FlightView>($"/flights/{id}?project={Uri.EscapeDataString(project)}", cancellationToken);
        }

        if (idOrPrefix.Length < 4)
        {
            throw new DomainException("Give at least four characters of the flight id.");
        }

        List<FlightView> flights = await client.GetAsync<List<FlightView>>($"/flights?project={Uri.EscapeDataString(project)}", cancellationToken);
        List<FlightView> matches = [.. flights.Where(f => Ids.Matches(f.Id, idOrPrefix))];
        return matches switch
        {
            [var one] => one,
            [] => throw new DomainException($"No flight in '{project}' matches '{idOrPrefix}'."),
            _ => throw new DomainException($"'{idOrPrefix}' matches {matches.Count} flights in '{project}'; give more characters."),
        };
    }
}
