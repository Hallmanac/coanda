using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Transport;

/// <summary>
/// Which of a project's stored events the import appended from another node's lines (<see cref="LedgerImport"/>).
/// Such an event is kept so this node knows what its peers did, and nothing on this node acts on it: the work
/// it records is its origin's, so the trigger loop starts nothing on it and the start-up advance leaves a flow
/// run it started alone. An event is imported when its <see cref="LedgerLine"/> names an origin other than
/// this node; the import saves the record in the same save as the event, so a reader that sees the event sees
/// its record, while an event this node recorded has no record until the export writes one under this node.
/// </summary>
public static class ImportedEvents
{
    /// <summary>The ids among <paramref name="eventIds"/> of events <paramref name="session"/>'s project holds from another origin than <paramref name="self"/>.</summary>
    public static async ValueTask<IReadOnlySet<Guid>> AmongAsync(IQuerySession session, Guid self, IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        Guid[] ids = [.. eventIds];
        IReadOnlyList<LedgerLine> lines = await session.Query<LedgerLine>()
            .Where(l => ids.Contains(l.Id) && l.Origin != self)
            .ToListAsync(cancellationToken);
        return lines.Select(l => l.Id).ToHashSet();
    }
}
