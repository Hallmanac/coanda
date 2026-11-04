using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Triggers.Events;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Triggers;

/// <summary>
/// The daemon's side of recording external events and subscriptions: pass the registry, ask
/// <see cref="TriggerDecider"/>, append, save. Both live in their project's tenant. Nothing records
/// an external event yet except tests; adapters arrive later.
/// </summary>
public sealed class TriggerService(IDocumentStore store, TimeProvider clock)
{
    public async Task<ExternalEvent> RecordEventAsync(
        Principal principal, string project, string kind, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("event.record", principal);
        string tenant = Tenants.RequireProject(project);
        ExternalEventRecorded recorded = TriggerDecider.RecordExternalEvent(tenant, kind, fields, principal.Id, clock.GetUtcNow());

        await using IDocumentSession session = store.LightweightSession(tenant);
        session.Events.StartStream<ExternalEvent>(recorded.EventId, recorded);
        await session.SaveChangesAsync(cancellationToken);
        return ExternalEvent.Create(recorded);
    }

    public async Task<Subscription> SubscribeAsync(
        Principal principal,
        string project,
        Trigger trigger,
        string parcelType,
        string titleField,
        string contour,
        CancellationToken cancellationToken)
    {
        CommandRegistry.Require("subscription.add", principal);
        string tenant = Tenants.RequireProject(project);
        SubscriptionRecorded recorded = TriggerDecider.RecordSubscription(tenant, trigger, parcelType, titleField, contour, principal, clock.GetUtcNow());

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

        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<Subscription> subscriptions = await session.Query<Subscription>().ToListAsync(cancellationToken);
        return [.. subscriptions.OrderBy(s => s.RecordedAt).ThenBy(s => s.Id)];
    }
}
