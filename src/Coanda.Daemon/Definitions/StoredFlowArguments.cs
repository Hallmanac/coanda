using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Daemon.Store;
using Coanda.Daemon.SystemSteps;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Definitions;

/// <summary>
/// The start's report of stored flow versions whose arguments for a module's step that step's argument
/// schema now refuses. Such a version was recorded before the check, or before its module's schema
/// narrowed, and it still reads, resolves, and runs as it did: the report changes nothing in the store.
/// </summary>
public static class StoredFlowArguments
{
    /// <summary>
    /// Logs one warning for each flow version, in any tenant, that is not retired and gives a module's step
    /// arguments the step's argument schema refuses, as <see cref="FlowSystemSteps.StoredArgumentComplaintsOf"/>
    /// reads the step. Runs once every module the node hosts has answered describe.
    /// </summary>
    public static async Task WarnAsync(IDocumentStore store, FlowSystemSteps systemSteps, ILogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyList<Definition> definitions;
        await using (IQuerySession query = store.QuerySession(Tenants.Node))
        {
            definitions = await query.Query<Definition>().AnyTenant().ToListAsync(cancellationToken);
        }

        IEnumerable<Definition> flows = definitions.Where(d => d.Kind == DefinitionKind.Flow && d.Status != DefinitionStatus.Retired);
        foreach (Definition definition in flows.OrderBy(d => d.Project, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.VersionNumber))
        {
            Flow flow;
            try
            {
                flow = Flow.FromJson(definition.Content);
            }
            catch (DomainException)
            {
                // A version that does not read as a flow is not this report's to describe.
                continue;
            }

            foreach (FlowStep step in flow.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.SystemStep))
                {
                    continue;
                }

                IReadOnlyList<StepArgumentComplaint> complaints = systemSteps.StoredArgumentComplaintsOf(step.SystemStep, step.Arguments);
                if (complaints.Count > 0)
                {
                    logger.LogWarning(
                        "Flow '{Flow}' version {Version} ({Scope} scope, project {Project}) gives step '{Step}' arguments the argument schema of the system step '{SystemStep}' refuses: {Complaint}. The version still runs as it did.",
                        definition.Name,
                        definition.VersionNumber,
                        definition.Scope.ToString().ToLowerInvariant(),
                        definition.Project ?? "none",
                        step.Name,
                        step.SystemStep,
                        string.Join("; ", complaints));
                }
            }
        }
    }
}
