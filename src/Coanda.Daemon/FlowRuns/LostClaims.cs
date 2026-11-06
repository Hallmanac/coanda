using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.FlowRuns;

/// <summary>
/// The daemon's stop of the flow runs this node started on parcels another node's claim now wins. The import
/// asks it after each round of a project, so once another node's winning claim arrives, a flow run this node
/// started on that parcel, running or paused, is stopped with a reason naming the winner
/// (<see cref="FlowRunService.YieldAsync"/>). The stop is the daemon's, never a module's (decision d97f5f86), and
/// the winner does not restart a binding for the entry the stopped flow run held (decision 2d0ab2d3).
/// </summary>
public sealed class LostClaims(IDocumentStore store, FlowRunService flowRuns, ILogger<LostClaims> logger)
{
    /// <summary>The principal the daemon stops a flow run on a lost parcel as.</summary>
    public static readonly Principal YieldPrincipal = new("coanda-claims", Role.Agent);

    /// <summary>
    /// Stops the flow runs this node started on parcels another node's claim wins, in every project, each apart:
    /// a project whose stop fails is logged and the others are still stopped. The daemon's start asks it before
    /// it advances any live flow run, since a daemon that stopped after storing a winning claim and before the
    /// import's own stop, or one that imports only on command, would otherwise carry on with a lost flow run.
    /// </summary>
    public async Task YieldAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Project> projects;
        await using (IQuerySession session = store.QuerySession(Tenants.Node))
        {
            projects = await session.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
        }

        foreach (Project project in projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            try
            {
                await YieldAsync(project.Name, cancellationToken);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(failure, "The flow runs on parcels another node's claim wins in project {Project} were not stopped at the start; the next import tries again.", project.Name);
            }
        }
    }

    /// <summary>Stops each live flow run this node started in <paramref name="project"/> on a parcel another node's claim wins; gives their ids.</summary>
    public async Task<IReadOnlyList<Guid>> YieldAsync(string project, CancellationToken cancellationToken)
    {
        string tenant = Tenants.RequireProject(project);
        List<Guid> lost = [];
        await using (IQuerySession session = store.QuerySession(tenant))
        {
            // Only claimed parcels with a live flow run can be lost, so the query reads those and not the project's history.
            IReadOnlyList<Parcel> parcels = await session.Query<Parcel>()
                .Where(p => p.LiveFlowRunId != null && p.Claimants.Any())
                .ToListAsync(cancellationToken);
            foreach (Parcel parcel in parcels)
            {
                Guid live = parcel.LiveFlowRunId.GetValueOrDefault();
                if (await ParcelClaims.StandingAsync(store, session, parcel, cancellationToken) is ClaimStanding.Lost
                    && await ImportedEvents.StartedElsewhereAsync(store, session, live, cancellationToken) is null)
                {
                    lost.Add(live);
                }
            }
        }

        List<Guid> stopped = [];
        foreach (Guid flowRunId in lost)
        {
            try
            {
                FlowRun yielded = await flowRuns.YieldAsync(YieldPrincipal, tenant, flowRunId, cancellationToken);
                logger.LogInformation(
                    "Stopped flow run {FlowRun} on parcel {Parcel} in project {Project}, since another node's claim wins the parcel.",
                    Ids.Short(yielded.Id), Ids.Short(yielded.ParcelId), tenant);
                stopped.Add(yielded.Id);
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Flow run {FlowRun} in project {Project} was not stopped for its lost claim: {Rule}", Ids.Short(flowRunId), tenant, refused.Message);
            }
        }

        return stopped;
    }
}
