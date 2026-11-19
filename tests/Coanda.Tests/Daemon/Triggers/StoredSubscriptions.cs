using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Triggers.Events;
using Fisher;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// Writes a subscription into a project's tenant as one recorded before triggers were definitions reads: no
/// command records a new one, but a subscription already in the store is still listed and still fires.
/// </summary>
public static class StoredSubscriptions
{
    public static async Task<Subscription> RecordAsync(
        IDocumentStore store, string project, Principal recordedBy, Trigger trigger, string parcelType, string titleField, string flow, CancellationToken cancellationToken)
    {
        var recorded = new SubscriptionRecorded(
            Ids.New(), project, trigger.EventKind, new Dictionary<string, string>(trigger.Filters, StringComparer.Ordinal),
            parcelType, titleField, flow, recordedBy.Id, recordedBy.Role, DateTimeOffset.UtcNow);
        await using IDocumentSession session = store.LightweightSession(project);
        session.Events.StartStream<Subscription>(recorded.SubscriptionId, recorded);
        await session.SaveChangesAsync(cancellationToken);
        return Subscription.Create(recorded);
    }
}
