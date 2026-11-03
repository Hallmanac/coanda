using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Fisher;

namespace Coanda.Tests.Daemon;

public sealed class StoreRoundTripTests
{
    [Fact]
    public async Task A_parcel_survives_closing_and_reopening_the_store_and_stays_in_its_project()
    {
        using var temp = new TemporaryHome();
        Guid id;

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var service = new ParcelService(store, TimeProvider.System);
            Parcel created = await service.AddAsync(Principal.LocalOwner, "alpha", "task", "Survive a restart", null, CancellationToken.None);
            id = created.Id;
            Parcel moved = await service.MoveAsync(Principal.LocalOwner, "alpha", id, "build", CancellationToken.None);
            moved.State.Should().Be("build");
        }

        await using (IDocumentStore reopened = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var service = new ParcelService(reopened, TimeProvider.System);

            Parcel? shown = await service.ShowAsync(Principal.LocalOwner, "alpha", id, CancellationToken.None);
            shown.Should().NotBeNull();
            shown.State.Should().Be("build");
            shown.Title.Should().Be("Survive a restart");

            IReadOnlyList<Parcel> alpha = await service.ListAsync(Principal.LocalOwner, "alpha", CancellationToken.None);
            alpha.Should().ContainSingle(p => p.Id == id);

            IReadOnlyList<Parcel> beta = await service.ListAsync(Principal.LocalOwner, "beta", CancellationToken.None);
            beta.Should().BeEmpty("the project is the tenant and another project sees nothing");

            Parcel? crossed = await service.ShowAsync(Principal.LocalOwner, "beta", id, CancellationToken.None);
            crossed.Should().BeNull();
        }
    }

    [Fact]
    public async Task The_registry_refuses_a_role_the_entry_does_not_allow()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var service = new ParcelService(store, TimeProvider.System);
        var agent = new Principal("build-session", Role.Agent);

        await FluentActions.Awaiting(() => service.AddAsync(agent, "alpha", "task", "x", null, CancellationToken.None))
            .Should().ThrowAsync<Coanda.Core.DomainException>().WithMessage("*not open to the Agent role*");
    }
}
