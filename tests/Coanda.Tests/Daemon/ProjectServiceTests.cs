using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Fisher;

namespace Coanda.Tests.Daemon;

public sealed class ProjectServiceTests
{
    private const string Remote = "git@github.com:you/coanda.git";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    [Fact]
    public async Task An_added_project_shows_and_lists_with_its_remote_and_branch_and_survives_reopening_the_store()
    {
        using var temp = new TemporaryHome();
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath))
        {
            Project added = await new ProjectService(store, TimeProvider.System).AddAsync(Owner, " coanda ", Remote, "main", None);
            added.Name.Should().Be("coanda");
            added.RegisteredBy.Should().Be(Owner.Id);
        }

        await using IDocumentStore reopened = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(reopened, TimeProvider.System);

        Project? shown = await projects.ShowAsync(Owner, "coanda", None);
        shown.Should().NotBeNull();
        shown.Name.Should().Be("coanda");
        shown.Remote.Should().Be(Remote);
        shown.PrimaryBranch.Should().Be("main");

        (await projects.ShowAsync(Owner, "  coanda", None)).Should().NotBeNull("names are compared after trimming");
        (await projects.ShowAsync(Owner, "Coanda", None)).Should().BeNull("no case folding is applied");
        (await projects.ShowAsync(Owner, "other", None)).Should().BeNull();
    }

    [Fact]
    public async Task The_list_holds_every_project_on_the_node_ordered_by_name_with_ordinal_comparison()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        await projects.AddAsync(Owner, "beta", "https://example.invalid/beta.git", "trunk", None);
        await projects.AddAsync(Owner, "alpha", "https://example.invalid/alpha.git", "main", None);
        await projects.AddAsync(Owner, "Gamma", "https://example.invalid/gamma.git", "main", None);

        IReadOnlyList<Project> listed = await projects.ListAsync(Owner, None);

        listed.Select(p => p.Name).Should().Equal("Gamma", "alpha", "beta");
        listed.Should().ContainSingle(p => p.Name == "beta" && p.Remote == "https://example.invalid/beta.git" && p.PrimaryBranch == "trunk");
    }

    [Fact]
    public async Task A_second_add_of_a_name_with_a_record_is_refused_even_when_it_differs_only_by_surrounding_space()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        await projects.AddAsync(Owner, "coanda", Remote, "main", None);

        await FluentActions.Awaiting(() => projects.AddAsync(Owner, " coanda ", "https://example.invalid/other.git", "trunk", None))
            .Should().ThrowAsync<DomainException>().WithMessage("Project 'coanda' is already registered*");

        (await projects.ListAsync(Owner, None)).Should().ContainSingle().Which.Remote.Should().Be(Remote);
    }

    [Fact]
    public async Task Two_concurrent_adds_of_one_name_leave_one_record()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);

        Task<Project>[] adds =
        [
            projects.AddAsync(Owner, "coanda", "https://example.invalid/a.git", "main", None),
            projects.AddAsync(Owner, "coanda", "https://example.invalid/b.git", "main", None),
        ];
        await FluentActions.Awaiting(() => Task.WhenAll(adds)).Should().ThrowAsync<DomainException>().WithMessage("*already registered*");

        adds.Count(a => a.IsCompletedSuccessfully).Should().Be(1);
        (await projects.ListAsync(Owner, None)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("", Remote, "main", "A project name is required*")]
    [InlineData("_node", Remote, "main", "*reserved for node-level records*")]
    [InlineData("_NODE", Remote, "main", "*reserved for node-level records*")]
    [InlineData("coanda", " ", "main", "*needs the remote of its repository*")]
    [InlineData("coanda", Remote, "", "*needs the primary branch of its repository*")]
    public async Task A_blank_name_the_reserved_name_a_blank_remote_or_a_blank_branch_is_refused(string name, string remote, string branch, string rule)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);

        await FluentActions.Awaiting(() => projects.AddAsync(Owner, name, remote, branch, None))
            .Should().ThrowAsync<DomainException>().WithMessage(rule);
        (await projects.ListAsync(Owner, None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Adding_a_project_is_refused_to_an_agent_and_showing_or_listing_is_open_to_one()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        var agent = new Principal("build-session", Role.Agent);
        var member = new Principal("teammate", Role.Member);

        await FluentActions.Awaiting(() => projects.AddAsync(agent, "coanda", Remote, "main", None))
            .Should().ThrowAsync<DomainException>().WithMessage("'project.add' is not open to the Agent role.");
        await FluentActions.Awaiting(() => projects.AddAsync(member, "coanda", Remote, "main", None))
            .Should().ThrowAsync<DomainException>().WithMessage("'project.add' is not open to the Member role.");

        await projects.AddAsync(Owner, "coanda", Remote, "main", None);
        (await projects.ShowAsync(agent, "coanda", None)).Should().NotBeNull();
        (await projects.ListAsync(agent, None)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("task")]
    [InlineData("no-such-type")]
    public async Task A_parcel_in_a_project_with_no_record_is_refused_naming_the_project_and_coa_project_add(string type)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var parcels = new ParcelService(store, TimeProvider.System);
        await RegisteredProjects.AddAsync(store, "alpha", None);

        await FluentActions.Awaiting(() => parcels.AddAsync(Owner, " beta ", type, "Nowhere to go", null, None))
            .Should().ThrowAsync<DomainException>().WithMessage("Project 'beta' is not registered; register it with coa project add beta*");
        (await parcels.ListAsync(Owner, "beta", None)).Should().BeEmpty();

        (await parcels.AddAsync(Owner, "alpha", "task", "Somewhere to go", null, None)).Project.Should().Be("alpha");
    }

    [Fact]
    public async Task A_blank_or_reserved_project_name_keeps_its_own_refusal_on_a_parcel_add()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var parcels = new ParcelService(store, TimeProvider.System);

        await FluentActions.Awaiting(() => parcels.AddAsync(Owner, " ", "task", "x", null, None))
            .Should().ThrowAsync<DomainException>().WithMessage("A project name is required*");
        await FluentActions.Awaiting(() => parcels.AddAsync(Owner, Tenants.Node, "task", "x", null, None))
            .Should().ThrowAsync<DomainException>().WithMessage("*reserved for node-level records*");
    }

    [Fact]
    public async Task A_parcel_an_unregistered_project_already_holds_still_moves_and_closes()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var parcels = new ParcelService(store, TimeProvider.System);

        // Written straight to the store, as a parcel created before projects had records was.
        ParcelCreated created = ParcelDecider.Create(ShippedTypes.Task, GateSet.Shipped, "legacy", "From before records", null, Owner.Id, DateTimeOffset.UtcNow);
        await using (IDocumentSession session = store.LightweightSession("legacy"))
        {
            session.Events.StartStream<Parcel>(created.ParcelId, created);
            await session.SaveChangesAsync(None);
        }

        await parcels.BriefAsync(Owner, "legacy", created.ParcelId, cancellationToken: None);
        (await parcels.MoveAsync(Owner, "legacy", created.ParcelId, "build", None)).State.Should().Be("build");
        (await parcels.CloseAsync(Owner, "legacy", created.ParcelId, "abandoned", "Never registered.", None)).Resolution.Should().Be("abandoned");
    }
}
