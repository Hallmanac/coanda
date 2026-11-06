using Fisher;

namespace Coanda.Daemon.Transport;

/// <summary>
/// How far this node's own chain in a project has been published, kept in the project's tenant as a node-local
/// record that is never exported, as two documents: <see cref="PushedId"/>, the last sequence of this node's lines
/// that a push to every clone of the project carried, and <see cref="FetchedId"/>, the pushed sequence as it
/// stood when a fetch of the peers' ledgers from every clone began and then succeeded, recorded once the import
/// of what it brought has finished and only when a root key record let them be trusted. A line at or below the
/// fetched sequence was pushed before a fetch that succeeded after it, which is what a claim needs before
/// this node holds its parcel (<see cref="Coanda.Core.Parcels.ClaimDecider"/>). The export writes the first and
/// the import the second, each under its own turn, so neither overwrites the other's.
/// </summary>
public sealed class LedgerProgress
{
    public const string PushedId = "pushed";

    public const string FetchedId = "fetched-after-push";

    public string Id { get; set; } = string.Empty;

    /// <summary>The sequence of this node's lines the record reaches, 0 for none.</summary>
    public long Through { get; set; }

    /// <summary>The sequence the record <paramref name="id"/> reaches in <paramref name="session"/>'s project, 0 when there is none.</summary>
    public static async Task<long> ThroughAsync(IQuerySession session, string id, CancellationToken cancellationToken) =>
        (await session.LoadAsync<LedgerProgress>(id, cancellationToken))?.Through ?? 0;

    /// <summary>Moves the record <paramref name="id"/> of <paramref name="project"/> on to <paramref name="through"/>, never back.</summary>
    public static async Task AdvanceAsync(IDocumentStore store, string project, string id, long through, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(project);
        LedgerProgress? held = await session.LoadAsync<LedgerProgress>(id, cancellationToken);
        if (held is not null && held.Through >= through)
        {
            return;
        }

        session.Store(new LedgerProgress { Id = id, Through = through });
        await session.SaveChangesAsync(cancellationToken);
    }
}
