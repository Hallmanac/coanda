using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Attention;

/// <summary>
/// The attention query: every paused flow run that needs a person on this node, in every project the
/// node holds or in one. Only a flow run this node started is listed, since a node writes only to the
/// flow runs it started (<see cref="ImportedEvents.StartedElsewhereAsync"/>) and a pause another node
/// recorded offers a lever this node refuses. The time each entry gives is that of the flow run's latest
/// <see cref="FlowRunPaused"/>, read from its stream, so it does not move with a later write to the paused
/// flow run and needs no property a document stored before this query existed would lack.
/// </summary>
/// <remarks>
/// A Module principal is answered only for the projects its module is switched on for, and a session
/// principal only for the project its session is bound to; naming any other project is refused with the
/// rule <see cref="ModuleScope"/> or <see cref="SessionScope"/> quotes. Every other principal, the daemon's
/// own Agent-role principals among them, is answered for every project.
/// </remarks>
public sealed class AttentionService(IDocumentStore store)
{
    /// <summary>
    /// The paused flow runs this node started, oldest pause first: in <paramref name="project"/> when it is
    /// given, which must be a project registered on the node, and in every project the principal may read
    /// otherwise.
    /// </summary>
    public async Task<IReadOnlyList<PausedFlowRun>> ListAsync(Principal principal, string? project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("attention.list", principal);
        IReadOnlyList<string> projects = project is null
            ? [.. (await ProjectService.VisibleAsync(store, principal, cancellationToken)).Select(p => p.Name)]
            : [await RequireReadableAsync(principal, project, cancellationToken)];

        List<PausedFlowRun> paused = [];
        foreach (string tenant in projects)
        {
            paused.AddRange(await PausedInAsync(tenant, cancellationToken));
        }

        return [.. paused
            .OrderBy(p => p.PausedAt)
            .ThenBy(p => p.Project, StringComparer.Ordinal)
            .ThenBy(p => p.FlowRunId)];
    }

    /// <summary>
    /// The project <paramref name="project"/> names, refused for a session principal bound to another, when
    /// the node has no record of it, and for a Module principal whose module is not switched on for it.
    /// </summary>
    private async Task<string> RequireReadableAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        string tenant = Tenants.RequireProject(project);
        SessionScope.RequireBoundProject(principal, tenant);
        if (await ProjectService.FindAsync(store, tenant, cancellationToken) is null)
        {
            throw new DomainException(ProjectService.Unregistered(tenant));
        }

        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        return tenant;
    }

    private async Task<IReadOnlyList<PausedFlowRun>> PausedInAsync(string tenant, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<FlowRun> flowRuns = await session.Query<FlowRun>()
            .Where(f => f.Status == FlowRunStatus.Paused)
            .ToListAsync(cancellationToken);

        List<PausedFlowRun> paused = [];
        foreach (FlowRun flowRun in flowRuns)
        {
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(flowRun.Id, token: cancellationToken);
            if (events.Count == 0
                || await ImportedEvents.OtherOriginAsync(store, session, events[0].Id, cancellationToken) is not null)
            {
                continue;
            }

            // A paused flow run's stream holds the pause that left it paused; the latest one is that pause.
            FlowRunPaused? latest = events.Select(e => e.Data).OfType<FlowRunPaused>().LastOrDefault();
            Parcel? parcel = await session.LoadAsync<Parcel>(flowRun.ParcelId, cancellationToken);
            paused.Add(new PausedFlowRun(
                tenant,
                flowRun.Id,
                flowRun.ParcelId,
                parcel?.Title ?? string.Empty,
                flowRun.FlowName,
                flowRun.CurrentStep,
                flowRun.PauseReason ?? string.Empty,
                flowRun.Lever ?? string.Empty,
                latest?.PausedAt ?? flowRun.UpdatedAt));
        }

        return paused;
    }
}
