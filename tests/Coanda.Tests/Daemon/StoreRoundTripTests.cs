using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Parcels;
using Fisher;

namespace Coanda.Tests.Daemon;

public sealed class StoreRoundTripTests
{
    [Fact]
    public async Task A_parcel_survives_closing_and_reopening_the_store_and_stays_in_its_project()
    {
        using var temp = new TemporaryHome();
        Guid id;

        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath))
        {
            var service = new ParcelService(store, TimeProvider.System);
            await RegisteredProjects.AddAsync(store, "alpha", CancellationToken.None);
            Parcel created = await service.AddAsync(Principal.LocalOwner, "alpha", "task", "Survive a restart", null, CancellationToken.None);
            id = created.Id;
            await service.BriefAsync(Principal.LocalOwner, "alpha", id, cancellationToken: CancellationToken.None);
            Parcel moved = await service.MoveAsync(Principal.LocalOwner, "alpha", id, "build", CancellationToken.None);
            Assert.Equal("build", moved.Stage);
        }

        await using (IDocumentStore reopened = await SeededStore.OpenAsync(temp.Home.DatabasePath))
        {
            var service = new ParcelService(reopened, TimeProvider.System);

            Parcel? shown = await service.ShowAsync(Principal.LocalOwner, "alpha", id, CancellationToken.None);
            Assert.NotNull(shown);
            Assert.Equal("build", shown.Stage);
            Assert.Equal("Survive a restart", shown.Title);
            Assert.Equivalent(TaskFields.ObjectiveValue, shown.ValueOf(TaskFields.Objective), strict: true);
            Assert.Equivalent(TaskFields.CriteriaValue, shown.ValueOf(TaskFields.Criteria), strict: true);
            Assert.Equivalent(TaskFields.PullRequestValue, shown.ValueOf(TaskFields.PullRequest), strict: true);
            Assert.Null(shown.ValueOf(TaskFields.NonGoals));

            IReadOnlyList<Parcel> alpha = await service.ListAsync(Principal.LocalOwner, "alpha", CancellationToken.None);
            Assert.Single(alpha, p => p.Id == id);

            IReadOnlyList<Parcel> beta = await service.ListAsync(Principal.LocalOwner, "beta", CancellationToken.None);
            // the project is the tenant and another project sees nothing
            Assert.Empty(beta);

            Parcel? crossed = await service.ShowAsync(Principal.LocalOwner, "beta", id, CancellationToken.None);
            Assert.Null(crossed);
        }
    }

    [Fact]
    public async Task The_registry_refuses_a_role_the_entry_does_not_allow()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var service = new ParcelService(store, TimeProvider.System);
        var agent = new Principal("build-session", Role.Agent);

        Coanda.Core.DomainException exception = await Assert.ThrowsAsync<Coanda.Core.DomainException>(() => service.AddAsync(agent, "alpha", "task", "x", null, CancellationToken.None));
        Assert.Contains("not open to the Agent role", exception.Message, StringComparison.Ordinal);
    }
}
