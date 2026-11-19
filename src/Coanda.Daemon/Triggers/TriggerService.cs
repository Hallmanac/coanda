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
/// The daemon's side of recording external events and subscriptions: pass the registry, ask
/// <see cref="TriggerDecider"/>, append, save; an external event already recorded under the same
/// project, kind, and external id is given back instead of appended. Both live in their project's tenant. Nothing records
/// an external event yet except tests; adapters arrive later. A Module principal records only in a
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

    public async Task<Subscription> SubscribeAsync(
        Principal principal,
        string project,
        Trigger trigger,
        string parcelType,
        string titleField,
        string flow,
        CancellationToken cancellationToken)
    {
        CommandRegistry.Require("subscription.add", principal);
        string tenant = Tenants.RequireProject(project);
        SubscriptionRecorded recorded = TriggerDecider.RecordSubscription(tenant, trigger, parcelType, titleField, flow, principal, clock.GetUtcNow());

        await using IDocumentSession session = store.LightweightSession(tenant);
        session.Events.StartStream<Subscription>(recorded.SubscriptionId, recorded);
        await session.SaveChangesAsync(cancellationToken);
        return Subscription.Create(recorded);
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
