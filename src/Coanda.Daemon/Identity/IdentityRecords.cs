using System.Text;
using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;
using Coanda.Daemon.Transport;
using Fisher;
using JasperFx.Events;

namespace Coanda.Daemon.Identity;

/// <summary>
/// A project's identity events in <see cref="IdentityTrust"/>'s input form, read from its tenant: each with
/// the key that signed its line, the line's origin, and its sequence.
/// </summary>
internal static class IdentityRecords
{
    /// <summary>
    /// Every identity event the project holds. One on a line is read as that line signed it; one this node
    /// recorded that the export has not yet put on a line is read, when <paramref name="self"/> is given, as this
    /// node's own, signed by its key at a sequence after every line, which is where its line will stand, and is
    /// left out otherwise, as import leaves out what it holds no line of.
    /// </summary>
    public static async Task<IReadOnlyList<IdentityRecord>> ReadAsync(IDocumentStore store, string tenant, NodeIdentity? self, CancellationToken cancellationToken)
    {
        string[] names = [.. typeof(IIdentityEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IIdentityEvent)))
            .Select(t => store.Options.EventGraph.EventMappingFor(t).EventTypeName)];
        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<IEvent> events = await session.Events.QueryEventsAsync(e => names.Contains(e.EventTypeName), cancellationToken);
        IReadOnlyDictionary<Guid, LedgerLine> lines = await ImportedEvents.LinesAsync(session, [.. events.Select(e => e.Id)], cancellationToken);
        List<IdentityRecord> records = [];
        foreach (IEvent entry in events)
        {
            if (entry.Data is not IIdentityEvent identityEvent)
            {
                continue;
            }

            if (lines.TryGetValue(entry.Id, out LedgerLine? line))
            {
                if (SignedLine.Parse(Encoding.UTF8.GetBytes(line.Line), out _) is { } signed)
                {
                    records.Add(new IdentityRecord(identityEvent, signed.Signer, line.Origin, line.Sequence));
                }
            }
            else if (self is not null)
            {
                records.Add(new IdentityRecord(identityEvent, self.Key.PublicKey, self.NodeId, long.MaxValue));
            }
        }

        return records;
    }
}
