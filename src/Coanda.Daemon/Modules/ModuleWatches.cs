using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Core.Projects;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Coanda.Sdk.Contract;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Logging;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Coanda.Daemon.Modules;

/// <summary>
/// Hands each hosted module, through its host adapter's watch call, the watches its projects' published
/// triggers compile to (<see cref="WatchCompilation"/>), so it knows which external events to report:
/// one watch request for every project it is switched on for, each replacing the module's whole set for
/// that project.
/// </summary>
/// <remarks>
/// A module unconfigured on this node for a project is sent an empty set for it, the request that says
/// "report nothing". Every request is checked against the module API's watch request schema before it is
/// sent. A request that fails, the module's own handling included, is logged with the module and the
/// project and stops neither that module's requests for its other projects nor any other module's.
/// Requests are sent only when the daemon starts, so a change after the start reaches the module at the
/// next start.
/// </remarks>
public sealed class ModuleWatches(
    IDocumentStore store,
    HostedModules modules,
    ModuleSettingsService settings,
    ILogger<ModuleWatches> logger)
{
    /// <summary>The module API's watch request schema, which the daemon's project embeds from <c>schemas/module/v1/</c>.</summary>
    private const string RequestSchemaResource = "Coanda.Daemon.Modules.watch-request.schema.json";

    private static readonly Lazy<Task<JsonSchema>> RequestSchema = new(ReadRequestSchemaAsync);

    private readonly DefinitionResolver definitions = new(store);

    /// <summary>
    /// Sends every hosted module, in name order, one watch request for each project it is switched on
    /// for, in name order. The daemon calls this once the modules have described themselves and their
    /// definitions have been imported, and before it writes its address file.
    /// </summary>
    public async Task SendAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Project> projects;
        await using (IQuerySession query = store.QuerySession(Tenants.Node))
        {
            projects = await query.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
        }

        foreach (string module in modules.Names.Order(StringComparer.Ordinal))
        {
            if (modules.AdapterOf(module) is not { } adapter || modules.DescribeOf(module) is not { } describe)
            {
                continue;
            }

            foreach (Project project in projects.Where(p => p.IsSwitchedOn(module)).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                try
                {
                    await SendAsync(module, adapter, describe, project, cancellationToken);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception failure)
                {
                    logger.LogError(failure, "The watch request for project '{Project}' to module '{Module}' failed.", project.Name, module);
                }
            }
        }
    }

    private async Task SendAsync(string module, ModuleHostAdapter adapter, DescribeAnswer describe, Project project, CancellationToken cancellationToken)
    {
        JsonSchema schema = await RequestSchema.Value.WaitAsync(cancellationToken);
        ModuleConfiguration configuration = await settings.StateAsync(project.Name, module, cancellationToken);
        CompiledWatches compiled;
        if (configuration.State == ModuleState.Unconfigured)
        {
            logger.LogInformation(
                "Sent module '{Module}' no watches for project '{Project}', for which it is unconfigured on this node; it needs {Missing}.",
                module, project.Name, string.Join(", ", configuration.Missing));
            compiled = new CompiledWatches(new WatchRequest(project.Name, []), []);
        }
        else
        {
            IReadOnlyList<Definition> triggers = await definitions.ResolveAllAsync(project.Name, DefinitionKind.Trigger, cancellationToken);
            compiled = WatchCompilation.Compile(project, module, describe, triggers, schema);
        }

        ICollection<ValidationError> failures = schema.Validate(JsonSerializer.Serialize(compiled.Request, ModuleJson.Options));
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"The watch request breaks the module API's watch request schema: {string.Join("; ", failures.Select(f => $"{f.Kind} at {f.Path ?? "#"}"))}.");
        }

        foreach (DroppedTrigger dropped in compiled.Dropped)
        {
            logger.LogWarning(
                "Left trigger '{Trigger}' of project '{Project}' out of the watch request to module '{Module}': {Reason}.",
                dropped.Trigger, project.Name, module, dropped.Reason);
        }

        await adapter.WatchAsync(compiled.Request, cancellationToken);
    }

    /// <summary>
    /// Reads the embedded watch request schema once for the whole process. It takes no cancellation token
    /// on purpose: <see cref="RequestSchema"/> caches the task it returns, so a token one caller cancelled
    /// would leave every later read with a cancelled schema.
    /// </summary>
    private static async Task<JsonSchema> ReadRequestSchemaAsync()
    {
        await using Stream stream = typeof(ModuleWatches).Assembly.GetManifestResourceStream(RequestSchemaResource)
            ?? throw new InvalidOperationException($"The daemon carries no '{RequestSchemaResource}'.");
        return await JsonSchema.FromJsonAsync(stream, CancellationToken.None);
    }
}
