using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Triggers.Events;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Triggers;

/// <summary>
/// The daemon's side of recording external events and listing subscriptions: pass the registry, ask
/// <see cref="TriggerDecider"/>, append, save; an external event already recorded under the same
/// project, kind, and external id is given back instead of appended. Both live in their project's tenant.
/// No new subscription is recorded, since a trigger is a definition now; the subscriptions already in the
/// store are still listed and still fire. An external event is recorded through the loopback API's
/// <c>/events</c> route, a module's chiefly. A Module principal records only in a
/// project its module is switched on for, and only kinds prefixed with its own module's name; a kind
/// prefixed with a module's name is recorded by that module alone.
/// </summary>
public sealed class TriggerService(IDocumentStore store, TimeProvider clock)
{
    private readonly SemaphoreSlim writes = new(1, 1);

    /// <summary>
    /// Records an external event, or gives back the one already recorded. Every rule is checked first,
    /// so a refused recording is refused whatever the record holds and its refusal carries nothing of
    /// it. Then, under the service's write gate, so that two recordings on this node cannot both
    /// append, an event of the project whose kind and external id are exactly
    /// <paramref name="kind"/> and <paramref name="externalId"/>, whether this node recorded it or
    /// imported it, is returned as it was recorded and nothing is appended. An event recorded before
    /// external ids were carried has none, so it matches no recording.
    /// </summary>
    public async Task<ExternalEvent> RecordEventAsync(
        Principal principal,
        string project,
        string kind,
        string? externalId,
        Guid? parcelId,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        CommandRegistry.Require("event.record", principal);
        string tenant = Tenants.RequireProject(project);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        ModuleScope.RequireOwnKind(principal, kind);
        ExternalEventRecorded recorded = TriggerDecider.RecordExternalEvent(tenant, kind, externalId, parcelId, fields, principal, clock.GetUtcNow());
        string matchKind = recorded.Kind;
        string? matchId = recorded.ExternalId;

        await writes.WaitAsync(cancellationToken);
        try
        {
            await using IDocumentSession session = store.LightweightSession(tenant);
            IReadOnlyList<ExternalEvent> matching = await session.Query<ExternalEvent>()
                .Where(e => e.Kind == matchKind && e.ExternalId == matchId)
                .ToListAsync(cancellationToken);
            if (matching.OrderBy(e => e.RecordedAt).ThenBy(e => e.Id).FirstOrDefault() is { } existing)
            {
                return existing;
            }

            session.Events.StartStream<ExternalEvent>(recorded.EventId, recorded);
            await session.SaveChangesAsync(cancellationToken);
            return ExternalEvent.Create(recorded);
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>The project's subscriptions, oldest first.</summary>
    public async Task<IReadOnlyList<Subscription>> ListSubscriptionsAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("subscription.list", principal);
        string tenant = Tenants.RequireProject(project);
        SessionScope.RequireBoundProject(principal, tenant);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);

        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<Subscription> subscriptions = await session.Query<Subscription>().ToListAsync(cancellationToken);
        return [.. subscriptions.OrderBy(s => s.RecordedAt).ThenBy(s => s.Id)];
    }
}
