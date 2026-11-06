using System.Text;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Transport;

/// <summary>
/// Which of a project's stored events the import appended from another node's lines (<see cref="LedgerImport"/>).
/// Such an event is kept so this node knows what its peers did, and records its origin's work, not this
/// node's: the trigger loop starts nothing on it unless this node holds its parcel, the start-up advance
/// leaves a flow run it started alone, the flow run service writes nothing to that flow run, and the module
/// feed delivers no end or stop of it. An event is imported when its <see cref="LedgerLine"/> names an origin
/// other than this node; the import saves the record in the same save as the event, so a reader that sees the
/// event sees its record, while an event this node recorded has no record until the export writes one under
/// this node, so having a record does not by itself mean the event was imported.
/// </summary>
public static class ImportedEvents
{
    /// <summary>The ids among <paramref name="eventIds"/> of events <paramref name="session"/>'s project holds from another origin than <paramref name="self"/>.</summary>
    public static async ValueTask<IReadOnlySet<Guid>> AmongAsync(IQuerySession session, Guid self, IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken) =>
        (await LinesAsync(session, eventIds, cancellationToken)).Values.Where(l => l.Origin != self).Select(l => l.Id).ToHashSet();

    /// <summary>
    /// The ledger records among <paramref name="eventIds"/> in <paramref name="session"/>'s project, by event id,
    /// own and imported alike; an event with none was recorded by this node and is not on a line yet.
    /// </summary>
    public static async ValueTask<IReadOnlyDictionary<Guid, LedgerLine>> LinesAsync(IQuerySession session, IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
        {
            return new Dictionary<Guid, LedgerLine>();
        }

        Guid[] ids = [.. eventIds];
        IReadOnlyList<LedgerLine> lines = await session.Query<LedgerLine>().Where(l => ids.Contains(l.Id)).ToListAsync(cancellationToken);
        return lines.ToDictionary(l => l.Id);
    }

    /// <summary>
    /// The id the origin's store gave the event this node's store holds as <paramref name="eventId"/>, as its line
    /// <paramref name="line"/> says: the same on every node that holds the event, where the id this node's store
    /// gave an imported event is this node's alone. An event with no line, or a line that does not parse, keeps
    /// <paramref name="eventId"/>, which for an event this node recorded is the origin's id.
    /// </summary>
    public static Guid OriginalIdOf(Guid eventId, LedgerLine? line) =>
        line is not null && SignedLine.Parse(Encoding.UTF8.GetBytes(line.Line), out _) is { } signed
            ? signed.Content.EventId
            : eventId;

    /// <summary>
    /// The node id this store exports its own lines under, read from the store rather than the node's identity so a
    /// service given only the store can tell its own lines from imported ones; null before the export's first line,
    /// when every line the store records is another origin's.
    /// </summary>
    public static async Task<Guid?> SelfAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        return (await session.LoadAsync<LedgerOrigin>(LedgerOrigin.DocumentId, cancellationToken))?.NodeId;
    }

    /// <summary>
    /// The origin of the event <paramref name="eventId"/> of <paramref name="session"/>'s project when another node
    /// recorded it, or null when this node did: it has no ledger record, or its record names the node this store
    /// exports under (<see cref="SelfAsync"/>).
    /// </summary>
    public static async Task<Guid?> OtherOriginAsync(IDocumentStore store, IQuerySession session, Guid eventId, CancellationToken cancellationToken)
    {
        LedgerLine? line = await session.LoadAsync<LedgerLine>(eventId, cancellationToken);
        if (line is null)
        {
            return null;
        }

        return line.Origin == await SelfAsync(store, cancellationToken)
            ? null
            : line.Origin;
    }

    /// <summary>
    /// The node that started the stream <paramref name="streamId"/> of <paramref name="session"/>'s project, such as
    /// a flow run, when another node did, or null when this node recorded its first event or the stream has none.
    /// </summary>
    public static async Task<Guid?> StartedElsewhereAsync(IDocumentStore store, IQuerySession session, Guid streamId, CancellationToken cancellationToken)
    {
        IReadOnlyList<IEvent> start = await session.Events.FetchStreamAsync(streamId, version: 1, token: cancellationToken);
        return start.Count == 0
            ? null
            : await OtherOriginAsync(store, session, start[0].Id, cancellationToken);
    }
}
