using System.ComponentModel;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api.Endpoints;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public sealed class DefinitionListCommand : AsyncCommand<ProjectSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        List<DefinitionView> definitions = await DefinitionLookup.ListAsync(client, settings.ResolveProject(), cancellationToken);
        var table = new Table().AddColumns(DefinitionRendering.DefinitionListColumns);
        foreach (DefinitionView definition in definitions)
        {
            table.AddRow(DefinitionRendering.DefinitionListRow(definition));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public class DefinitionIdSettings : ProjectSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The definition's id or its short form, the last eight characters, which coa definition list prints.")]
    public required string Id { get; init; }
}

public sealed class DefinitionShowCommand : AsyncCommand<DefinitionIdSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DefinitionIdSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        Guid id = await DefinitionLookup.IdOfAsync(client, project, settings.Id, cancellationToken);
        DefinitionDetailView shown = await client.GetAsync<DefinitionDetailView>(
            $"/definitions/{id}?project={Uri.EscapeDataString(project)}", cancellationToken);
        DefinitionOutput.Write(shown.Definition);
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(shown.Content);
        return 0;
    }
}

public sealed class DefinitionDraftSettings : ProjectSettings
{
    [CommandArgument(0, "<KIND>")]
    [Description("The definition's kind: parcel-type, flow, agent, or trigger.")]
    public required string Kind { get; init; }

    [CommandArgument(1, "<FILE>")]
    [Description("A JSON file holding the definition, in the shape a module ships for its kind.")]
    public required string File { get; init; }

    [CommandOption("--scope <SCOPE>")]
    [Description("Where the definition applies: project, the default, or owner. The daemon decides what a scope allows.")]
    public string? Scope { get; init; }

    [CommandOption("--fork")]
    [Description("Fork the module-scope definition of the same kind and name this node holds, its highest version.")]
    public bool Fork { get; init; }
}

public sealed class DefinitionDraftCommand : AsyncCommand<DefinitionDraftSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DefinitionDraftSettings settings, CancellationToken cancellationToken)
    {
        string content = System.IO.File.Exists(settings.File)
            ? await System.IO.File.ReadAllTextAsync(settings.File, cancellationToken)
            : throw new DomainException($"There is no file at '{settings.File}'.");
        using var client = new DaemonClient(NodeHome.Resolve());
        DefinitionView drafted = await client.PostAsync<DefinitionView>(
            "/definitions",
            new DefinitionDraftRequest(settings.ResolveProject(), settings.Kind, content, settings.Scope, settings.Fork),
            cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Drafted {drafted.Kind} {drafted.Name} version {drafted.Version}; publish it with coa definition publish {Ids.Short(drafted.Id)}.");
        DefinitionOutput.Write(drafted);
        return 0;
    }
}

public sealed class DefinitionPublishCommand : AsyncCommand<DefinitionIdSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DefinitionIdSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        Guid id = await DefinitionLookup.IdOfAsync(client, project, settings.Id, cancellationToken);
        DefinitionView published = await client.PostAsync<DefinitionView>("/definitions/publish", new DefinitionIdRequest(project, id), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Published {published.Kind} {published.Name} version {published.Version}.");
        DefinitionOutput.Write(published);
        return 0;
    }
}

public sealed class DefinitionRetireCommand : AsyncCommand<DefinitionIdSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DefinitionIdSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        Guid id = await DefinitionLookup.IdOfAsync(client, project, settings.Id, cancellationToken);
        DefinitionView retired = await client.PostAsync<DefinitionView>("/definitions/retire", new DefinitionIdRequest(project, id), cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Retired {retired.Kind} {retired.Name} version {retired.Version}.");
        DefinitionOutput.Write(retired);
        return 0;
    }
}

internal static class DefinitionOutput
{
    public static void Write(DefinitionView definition)
    {
        var table = new Table().HideHeaders().AddColumns("k", "v");
        foreach (string[] row in DefinitionRendering.DefinitionRows(definition))
        {
            table.AddRow(row);
        }

        AnsiConsole.Write(table);
    }
}

/// <summary>
/// Resolves the id a person gives to a definition's full id on the client, from the project's list:
/// the routes take only full ids.
/// </summary>
internal static class DefinitionLookup
{
    public static Task<List<DefinitionView>> ListAsync(DaemonClient client, string project, CancellationToken cancellationToken) =>
        client.GetAsync<List<DefinitionView>>($"/definitions?project={Uri.EscapeDataString(project)}", cancellationToken);

    /// <summary>Accepts a full id, the short form (last eight characters), or at least four characters of either end.</summary>
    public static async Task<Guid> IdOfAsync(DaemonClient client, string project, string idOrPrefix, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrPrefix, out Guid id))
        {
            return id;
        }

        if (idOrPrefix.Length < 4)
        {
            throw new DomainException("Give at least four characters of the definition id.");
        }

        List<DefinitionView> definitions = await ListAsync(client, project, cancellationToken);
        List<DefinitionView> matches = [.. definitions.Where(d => Ids.Matches(d.Id, idOrPrefix))];
        return matches switch
        {
            [var one] => one.Id,
            [] => throw new DomainException($"No definition that can govern '{project}' matches '{idOrPrefix}'."),
            _ => throw new DomainException($"'{idOrPrefix}' matches {matches.Count} definitions that can govern '{project}'; give more characters."),
        };
    }
}
