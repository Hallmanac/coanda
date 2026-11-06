using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Fisher;

namespace Coanda.Daemon.Parcels;

/// <summary>
/// The daemon's side of <c>coa parcel claim</c>: records this node's claim on a parcel, unless it has one,
/// then publishes and fetches in that order, the export's push of this node's ledger and the import's fetch of
/// its peers', and reads where the claims leave this node. Run again, it records nothing and runs the push and
/// the fetch again, so a claim not yet held can be carried on to held by the same command.
/// </summary>
public sealed class ParcelClaimant(IDocumentStore store, ParcelService parcels, LedgerExport export, LedgerImport import, NodeIdentity identity)
{
    public async Task<ParcelClaimResult> ClaimAsync(Principal principal, string project, Guid parcelId, CancellationToken cancellationToken)
    {
        string tenant = Tenants.RequireProject(project);
        (Parcel parcel, bool recorded) = await parcels.ClaimAsync(principal, tenant, parcelId, identity.NodeId, cancellationToken);
        LedgerPublication published = await export.PublishAsync(tenant, cancellationToken);
        LedgerImportReport fetched = await import.ImportAsync(tenant, cancellationToken);

        ClaimStanding standing;
        await using (IQuerySession session = store.QuerySession(tenant))
        {
            Parcel now = await session.LoadAsync<Parcel>(parcel.Id, cancellationToken) ?? parcel;
            standing = await ParcelClaims.StandingAsync(store, session, now, cancellationToken);
        }

        // A round that trusted no peer's line read none of their claims, so its reason says why the claim is not held.
        IEnumerable<ImportFault> untrusted = fetched.Origins
            .Select(o => o.Fault)
            .OfType<ImportFault>()
            .Where(f => f.Kind is ImportFaultKind.NoTrustedRoot)
            .Take(1);
        string[] failures =
        [
            .. published.Failures.Select(f => $"push: {f}"),
            .. fetched.Failures.Select(f => $"fetch: {f}"),
            .. untrusted.Select(f => $"fetch: {f.Reason}"),
        ];
        return standing switch
        {
            ClaimStanding.Held => new ParcelClaimResult(parcel.Id, recorded, ParcelClaimResult.Held, null, null),
            ClaimStanding.Lost lost => new ParcelClaimResult(parcel.Id, recorded, ParcelClaimResult.Lost, lost.Winner, null),
            ClaimStanding.NotYetHeld pending => new ParcelClaimResult(
                parcel.Id, recorded, ParcelClaimResult.NotYetHeld, null, failures.Length > 0 ? string.Join(" ", failures) : pending.Reason),
            _ => throw new InvalidOperationException($"Parcel {Ids.Short(parcel.Id)} holds this node's claim and reads as unclaimed."),
        };
    }
}

/// <summary>
/// What a claim came to: the parcel, whether this call recorded the claim, and one of three results: held;
/// lost, with the winning node; or not yet held, with why, the reason the push or the fetch failed when one did.
/// </summary>
public sealed record ParcelClaimResult(Guid ParcelId, bool Recorded, string Result, Guid? Winner, string? Reason)
{
    public const string Held = "held";

    public const string Lost = "lost";

    public const string NotYetHeld = "not-yet-held";
}
