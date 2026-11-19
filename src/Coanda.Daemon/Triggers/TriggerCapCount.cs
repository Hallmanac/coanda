using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Triggers;
using Coanda.Daemon.Transport;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Triggers;

/// <summary>
/// How many parcels a trigger created in a project in the hour before a time, which its hourly cap is weighed
/// against. The count is by the trigger's name, compared without regard to case as resolution compares names, over
/// every parcel the project's record holds whose origin names a firing of a trigger of that name, whatever its
/// scope, open or closed, created here or imported. A parcel counts while the time the count takes for its creation
/// is later than an hour before: its recorded creation time, or, for a parcel imported from another node whose
/// recorded creation time was later than this node's clock at the import, the time of the import.
/// </summary>
internal static class TriggerCapCount
{
    /// <summary>The sliding window a trigger's hourly cap counts over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public static async Task<int> CountAsync(IDocumentStore store, string project, string trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset since = now - Window;
        await using IQuerySession session = store.QuerySession(project);
        IReadOnlyList<Parcel> created = await session.Query<Parcel>()
            .Where(p => p.Origin != null && p.Origin.StartsWith(TriggerKey.Prefix))
            .ToListAsync(cancellationToken);

        // The time a count takes for a creation is never later than its recorded one, so a parcel recorded as created
        // before the window counts in no case.
        Parcel[] named = [.. created.Where(p => p.CreatedAt > since
            && p.Origin is { } origin
            && TriggerKey.NameOf(origin, project) is { } name
            && DefinitionResolution.SameName(name, trigger))];
        if (named.Length == 0)
        {
            return 0;
        }

        Dictionary<Guid, Guid> creations = [];
        foreach (Parcel parcel in named)
        {
            IReadOnlyList<IEvent> first = await session.Events.FetchStreamAsync(parcel.Id, 1, token: cancellationToken);
            if (first.Count > 0)
            {
                creations[parcel.Id] = first[0].Id;
            }
        }

        IReadOnlyDictionary<Guid, LedgerLine> lines = await ImportedEvents.LinesAsync(session, creations.Values, cancellationToken);
        return named.Count(parcel => CountedAt(parcel, creations, lines) > since);
    }

    /// <summary>
    /// The time the count takes for the parcel's creation: the earlier of its recorded creation time and the time this
    /// node imported it, for a parcel whose line carries the time of its import, and its recorded creation time otherwise.
    /// </summary>
    private static DateTimeOffset CountedAt(Parcel parcel, IReadOnlyDictionary<Guid, Guid> creations, IReadOnlyDictionary<Guid, LedgerLine> lines) =>
        creations.TryGetValue(parcel.Id, out Guid creation)
            && lines.TryGetValue(creation, out LedgerLine? line)
            && line.ImportedAt is { } importedAt
            && importedAt < parcel.CreatedAt
            ? importedAt
            : parcel.CreatedAt;
}
