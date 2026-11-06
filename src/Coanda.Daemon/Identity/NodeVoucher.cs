using Coanda.Core;
using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Fisher;
using JasperFx.Events;

namespace Coanda.Daemon.Identity;

/// <summary>
/// The daemon's side of <c>coa node vouch</c>: records a vouch for a node id and its exact key in a project's
/// tenant, then exports and pushes the project before it answers. The first vouch in a project this node
/// registered itself, while the project holds no owner root, first records the owner root naming this node and
/// its key, and writes this node's trusted root key record (<see cref="LedgerRoot"/>) naming that key, so the
/// owner's node imports what the nodes it vouches for push. Run again with the same node and key, it records
/// nothing and pushes again.
/// </summary>
public sealed class NodeVoucher(IDocumentStore store, LedgerExport export, NodeIdentity identity, TimeProvider clock)
{
    private readonly SemaphoreSlim writes = new(1, 1);

    /// <summary>Vouches for the node <paramref name="nodeId"/>, a hyphenated UUID as <c>coa node show</c> prints it, with the key <paramref name="key"/>.</summary>
    public Task<NodeVouchResult> VouchAsync(Principal principal, string project, string nodeId, string key, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("node.vouch", principal);
        return Guid.TryParseExact(nodeId.Trim(), "D", out Guid parsed)
            ? VouchAsync(principal, project, parsed, key, cancellationToken)
            : throw new DomainException($"'{nodeId}' is not a node id; a node id is a hyphenated UUID, as coa node show prints it.");
    }

    public async Task<NodeVouchResult> VouchAsync(Principal principal, string project, Guid nodeId, string key, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("node.vouch", principal);
        string tenant = Tenants.RequireProject(project);

        bool recorded;
        bool rootRecorded = false;
        await writes.WaitAsync(cancellationToken);
        try
        {
            Project registered = await ProjectService.FindAsync(store, tenant, cancellationToken)
                ?? throw new DomainException(ProjectDecider.Unregistered(tenant));
            IReadOnlyList<IdentityRecord> records = await IdentityRecords.ReadAsync(store, tenant, identity, cancellationToken);
            DateTimeOffset now = clock.GetUtcNow();
            NodeVouched? vouch = IdentityDecider.Vouch(records, identity.NodeId, identity.Key.PublicKey, nodeId, key, now);
            recorded = vouch is not null;
            if (vouch is not null)
            {
                List<object> events = [];
                await using IDocumentSession session = store.LightweightSession(tenant);
                if (!records.Any(r => r.Event is OwnerRootRecorded))
                {
                    await RequireOwnRegistrationAsync(session, registered, cancellationToken);
                    string own = identity.Key.PublicKey;
                    if (await session.LoadAsync<LedgerRoot>(LedgerRoot.DocumentId, cancellationToken) is { } trusted && trusted.Key != own)
                    {
                        throw new DomainException(
                            $"Project '{tenant}' has no owner root, and this node's trusted root key record names {trusted.Key}, not this node's own key {own}; the owner's node records the root with its own key, so this node records none.");
                    }

                    events.Add(IdentityDecider.RecordOwnerRoot(own, records, identity.NodeId, own, now));
                    session.Store(new LedgerRoot { Key = own });
                    rootRecorded = true;
                }

                events.Add(vouch);
                session.Events.StartStream(Ids.New(), [.. events]);
                await session.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            writes.Release();
        }

        LedgerPublication published = await export.PublishAsync(tenant, cancellationToken);
        return new NodeVouchResult(
            nodeId,
            key,
            recorded,
            rootRecorded,
            published.Published,
            published.Published
                ? null
                : string.Join(" ", published.Failures));
    }

    /// <summary>
    /// Refuses the owner root in a project whose registration this node imported: the owner's node records the
    /// root with its first vouch, and this node waits for it to arrive.
    /// </summary>
    private async Task RequireOwnRegistrationAsync(IDocumentSession session, Project project, CancellationToken cancellationToken)
    {
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(project.Id, token: cancellationToken);
        if (await ProjectService.ImportedAsync(store, session, events, cancellationToken))
        {
            throw new DomainException(
                $"Project '{project.Name}' holds no owner root yet, and this node's registration of it was imported from the owner's node; the owner's node records the root with its first vouch, so this node vouches only once that root has arrived.");
        }
    }
}

/// <summary>
/// What a vouch came to: the node and key, whether this call recorded the vouch, whether it recorded the
/// project's owner root first, and whether the export pushed the project, with the push's reason when it did not.
/// </summary>
public sealed record NodeVouchResult(Guid NodeId, string Key, bool Recorded, bool RootRecorded, bool Pushed, string? Reason);
