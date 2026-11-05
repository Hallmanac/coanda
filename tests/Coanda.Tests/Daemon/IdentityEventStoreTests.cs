using Coanda.Core;
using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;
using Coanda.Daemon.Store;
using Coanda.Tests.Core.Identity;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon;

public sealed class IdentityEventStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Each_identity_event_is_held_in_a_project_tenant_and_read_back_unchanged()
    {
        using var temp = new TemporaryHome();
        DateTimeOffset at = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        IIdentityEvent[] written =
        [
            new OwnerRootRecorded(Parties.Root.Node, Parties.Root.Key, at),
            new NodeVouched(Parties.A.Node, Parties.A.Key, at.AddSeconds(1)),
            new NodeRevoked(Parties.A.Node, Parties.A.Key, at.AddSeconds(2)),
            new MemberAdded(Parties.M.Node, Parties.M.Key, MemberRole.Owner, at.AddSeconds(3)),
            new MemberAdded(Parties.N.Node, Parties.N.Key, MemberRole.Member, at.AddSeconds(4)),
            new MemberRemoved(Parties.M.Key, at.AddSeconds(5)),
            new SuccessorNamed(Parties.S.Node, Parties.S.Key, at.AddSeconds(6)),
            new RootRotated(Parties.S.Key, Parties.Root.Key, at.AddSeconds(7)),
            new SuccessorRevoked(Parties.S.Key, at.AddSeconds(8)),
        ];

        Guid streamId = Ids.New();
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await using IDocumentSession session = store.LightweightSession("alpha");
            session.Events.StartStream(streamId, [.. written.Cast<object>()]);
            await session.SaveChangesAsync(None);
        }

        await using IDocumentStore reopened = StoreConfiguration.Open(temp.Home.DatabasePath);
        await using IQuerySession project = reopened.QuerySession("alpha");
        IReadOnlyList<IEvent> read = await project.Events.FetchStreamAsync(streamId, token: None);
        Assert.Equal(written, read.Select(e => e.Data));

        // The project's tenant holds them, never the node's.
        await using IQuerySession node = reopened.QuerySession(Tenants.Node);
        Assert.Empty(await node.Events.FetchStreamAsync(streamId, token: None));
    }
}
