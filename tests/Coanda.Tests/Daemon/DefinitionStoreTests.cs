using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Core.Types;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Tests.Daemon;

public sealed class DefinitionStoreTests
{
    private static readonly Principal Agent = new("build-session", Role.Agent);
    private static readonly Principal Member = new("pat", Role.Member);

    private static readonly ParcelType QaTask = ShippedTypes.Task with
    {
        States = [.. ShippedTypes.Task.States, "qa"],
        Moves = new Dictionary<string, IReadOnlyList<string>>
        {
            ["refine"] = ["build"],
            ["build"] = ["review"],
            ["review"] = ["build", "qa"],
        },
    };

    private static DefinitionService Definitions(IDocumentStore store) => new(store, TimeProvider.System, actions: ShippedActions.Names);

    private static async Task<Definition> PublishedProjectTypeAsync(DefinitionService definitions, string project, ParcelType type)
    {
        Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, project, type, null, CancellationToken.None);
        return await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, CancellationToken.None);
    }

    private static async Task<long> CountEventsAsync(IDocumentStore store, params string[] tenants)
    {
        long count = 0;
        foreach (string tenant in tenants)
        {
            await using IQuerySession session = store.QuerySession(tenant);
            foreach (Definition definition in await session.Query<Definition>().ToListAsync(CancellationToken.None))
            {
                count += (await session.Events.FetchStreamAsync(definition.Id, token: CancellationToken.None)).Count;
            }
        }

        return count;
    }

    [Fact]
    public async Task A_definition_reads_back_with_every_field_unchanged_after_the_store_is_reopened()
    {
        using var temp = new TemporaryHome();
        Definition project;
        Definition owner;
        ParcelType fork = QaTask;

        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath))
        {
            DefinitionService definitions = Definitions(store);
            project = await definitions.DraftParcelTypeAsync(
                Principal.LocalOwner, DefinitionScope.Project, "alpha", fork, new ForkBase("task", 1), CancellationToken.None);
            project = await definitions.PublishAsync(Principal.LocalOwner, "alpha", project.Id, CancellationToken.None);
            owner = await definitions.DraftParcelTypeAsync(
                Principal.LocalOwner, DefinitionScope.Owner, null, ShippedTypes.Idea with { Name = "note" }, null, CancellationToken.None);
        }

        await using (IDocumentStore reopened = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await using IQuerySession alpha = reopened.QuerySession("alpha");
            Definition? read = await alpha.LoadAsync<Definition>(project.Id, CancellationToken.None);
            read.Should().NotBeNull();
            read.Kind.Should().Be(DefinitionKind.ParcelType);
            read.Name.Should().Be("task");
            read.Scope.Should().Be(DefinitionScope.Project);
            read.Project.Should().Be("alpha");
            read.VersionNumber.Should().Be(1);
            read.Content.Should().Be(ParcelType.CanonicalJson(fork));
            read.ContentHash.Should().Be(fork.Hash);
            read.Status.Should().Be(DefinitionStatus.Published);
            read.ForkBase.Should().Be(new ForkBase("task", 1));

            await using IQuerySession node = reopened.QuerySession(Tenants.Node);
            Definition? readOwner = await node.LoadAsync<Definition>(owner.Id, CancellationToken.None);
            readOwner.Should().NotBeNull();
            readOwner.Scope.Should().Be(DefinitionScope.Owner);
            readOwner.Project.Should().BeNull();
            readOwner.Status.Should().Be(DefinitionStatus.Draft);
            readOwner.ForkBase.Should().BeNull();

            IReadOnlyList<Definition> shipped = [.. (await node.Query<Definition>().ToListAsync(CancellationToken.None)).Where(d => d.Scope == DefinitionScope.Shipped)];
            shipped.Select(d => (d.Name, d.ContentHash, d.VersionNumber, d.Status)).Should().BeEquivalentTo(
                ShippedTypes.All.Select(t => (t.Name, t.Hash, 1, DefinitionStatus.Published)));
        }
    }

    [Fact]
    public async Task An_owner_scope_draft_may_fork_a_seeded_shipped_version()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);

        Definition draft = await Definitions(store).DraftParcelTypeAsync(
            Principal.LocalOwner, DefinitionScope.Owner, null, QaTask, new ForkBase("task", 1), CancellationToken.None);

        draft.Scope.Should().Be(DefinitionScope.Owner);
        draft.ForkBase.Should().Be(new ForkBase("task", 1));
    }

    [Theory]
    [InlineData(DefinitionScope.Project, "alpha")]
    [InlineData(DefinitionScope.Owner, null)]
    public async Task A_draft_whose_fork_base_names_no_seeded_shipped_version_is_refused(DefinitionScope scope, string? project)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        DefinitionService definitions = Definitions(store);

        foreach (ForkBase unshipped in new[] { new ForkBase("task", 2), new ForkBase("bug", 1) })
        {
            await FluentActions.Awaiting(() => definitions.DraftParcelTypeAsync(
                    Principal.LocalOwner, scope, project, QaTask, unshipped, CancellationToken.None))
                .Should().ThrowAsync<DomainException>()
                .WithMessage($"The fork base '{unshipped.Name}' version {unshipped.Version} of 'task' names no shipped parcel type*");
        }

        // A shipped definition of another kind is not a fork base for a parcel type.
        await definitions.SeedAsync(ShippedDefinitions.Contours, CancellationToken.None);
        await FluentActions.Awaiting(() => definitions.DraftParcelTypeAsync(
                Principal.LocalOwner, scope, project, QaTask, new ForkBase("build", 1), CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("The fork base 'build' version 1*");
    }

    [Fact]
    public async Task Versions_count_from_one_per_name_and_per_project_and_a_draft_does_not_resolve()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        DefinitionService definitions = Definitions(store);
        var resolver = new DefinitionResolver(store);

        Definition first = await definitions.DraftParcelTypeAsync(Agent, DefinitionScope.Project, "alpha", QaTask, null, CancellationToken.None);
        Definition second = await definitions.DraftParcelTypeAsync(Agent, DefinitionScope.Project, "alpha", QaTask, null, CancellationToken.None);
        Definition otherProject = await definitions.DraftParcelTypeAsync(Agent, DefinitionScope.Project, "beta", QaTask, null, CancellationToken.None);
        Definition owner = await definitions.DraftParcelTypeAsync(Agent, DefinitionScope.Owner, null, QaTask, null, CancellationToken.None);

        first.VersionNumber.Should().Be(1);
        second.VersionNumber.Should().Be(2);
        otherProject.VersionNumber.Should().Be(1);
        owner.VersionNumber.Should().Be(1);

        Definition? resolved = await resolver.ResolveAsync("alpha", DefinitionKind.ParcelType, "task", CancellationToken.None);
        resolved.Should().NotBeNull();
        resolved.Scope.Should().Be(DefinitionScope.Shipped, "an agent's drafts govern nothing");
        resolved.ContentHash.Should().Be(ShippedTypes.Task.Hash);
    }

    [Theory]
    [InlineData(Role.Member)]
    [InlineData(Role.Agent)]
    public async Task Only_the_owner_role_publishes_or_retires(Role role)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        DefinitionService definitions = Definitions(store);
        var principal = new Principal("someone", role);

        Definition draft = await definitions.DraftParcelTypeAsync(principal, DefinitionScope.Project, "alpha", QaTask, null, CancellationToken.None);

        await FluentActions.Awaiting(() => definitions.PublishAsync(principal, "alpha", draft.Id, CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage($"*not open to the {role} role*");
        await FluentActions.Awaiting(() => definitions.RetireAsync(principal, "alpha", draft.Id, CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage($"*not open to the {role} role*");
    }

    [Fact]
    public async Task Publishing_and_retiring_change_what_resolution_returns()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        DefinitionService definitions = Definitions(store);
        var resolver = new DefinitionResolver(store);

        Definition published = await PublishedProjectTypeAsync(definitions, "alpha", QaTask);
        (await resolver.ResolveAsync("alpha", DefinitionKind.ParcelType, "TASK", CancellationToken.None))!.Id.Should().Be(published.Id);

        await definitions.RetireAsync(Principal.LocalOwner, "alpha", published.Id, CancellationToken.None);
        Definition? afterRetire = await resolver.ResolveAsync("alpha", DefinitionKind.ParcelType, "task", CancellationToken.None);
        afterRetire!.Scope.Should().Be(DefinitionScope.Shipped);

        await FluentActions.Awaiting(() => definitions.PublishAsync(Principal.LocalOwner, "alpha", published.Id, CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("Only a draft can be published*");
    }

    [Fact]
    public async Task A_shipped_definition_cannot_be_retired_through_the_service()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var resolver = new DefinitionResolver(store);
        Definition shipped = (await resolver.ResolveAsync("alpha", DefinitionKind.ParcelType, "task", CancellationToken.None))!;

        await FluentActions.Awaiting(() => Definitions(store).RetireAsync(Principal.LocalOwner, null, shipped.Id, CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("A shipped definition cannot be retired*");
    }

    [Fact]
    public async Task Owner_and_shipped_records_live_in_the_node_tenant_that_no_project_name_reaches()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        DefinitionService definitions = Definitions(store);
        var parcels = new ParcelService(store, TimeProvider.System);
        await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Owner, null, QaTask, null, CancellationToken.None);

        await using (IQuerySession project = store.QuerySession("alpha"))
        {
            (await project.Query<Definition>().ToListAsync(CancellationToken.None)).Should().BeEmpty("a project tenant holds only its own definitions");
        }

        await using (IQuerySession node = store.QuerySession(Tenants.Node))
        {
            (await node.Query<Definition>().ToListAsync(CancellationToken.None)).Should().HaveCount(ShippedTypes.All.Count + 1);
        }

        foreach (string reserved in new[] { Tenants.Node, " _NODE " })
        {
            await FluentActions.Awaiting(() => parcels.ListAsync(Principal.LocalOwner, reserved, CancellationToken.None))
                .Should().ThrowAsync<DomainException>().WithMessage("*reserved for node-level records*");
            await FluentActions.Awaiting(() => parcels.AddAsync(Principal.LocalOwner, reserved, "task", "x", null, CancellationToken.None))
                .Should().ThrowAsync<DomainException>().WithMessage("*reserved for node-level records*");
            await FluentActions.Awaiting(() => parcels.TypesAsync(Principal.LocalOwner, reserved, CancellationToken.None))
                .Should().ThrowAsync<DomainException>().WithMessage("*reserved for node-level records*");
            await FluentActions.Awaiting(() => definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, reserved, QaTask, null, CancellationToken.None))
                .Should().ThrowAsync<DomainException>().WithMessage("*reserved for node-level records*");
        }
    }

    [Fact]
    public async Task Seeding_a_second_time_appends_no_events()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        long before = await CountEventsAsync(store, Tenants.Node);
        before.Should().Be(ShippedTypes.All.Count * 2, "each shipped type is a drafted and a published event");

        IReadOnlyList<Definition> again = await Definitions(store).SeedAsync(ShippedDefinitions.ParcelTypes, CancellationToken.None);

        again.Should().BeEmpty();
        (await CountEventsAsync(store, Tenants.Node)).Should().Be(before);
    }

    [Fact]
    public async Task A_shipped_type_with_a_new_hash_is_seeded_as_the_next_version_and_resolution_returns_it()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        ParcelType changed = ShippedTypes.Task with { Resolutions = [.. ShippedTypes.Task.Resolutions, "wontfix"] };

        IReadOnlyList<Definition> seeded = await Definitions(store).SeedAsync([ShippedDefinition.FromParcelType(changed)], CancellationToken.None);

        seeded.Should().ContainSingle().Which.VersionNumber.Should().Be(2);
        Definition? resolved = await new DefinitionResolver(store).ResolveAsync("alpha", DefinitionKind.ParcelType, "task", CancellationToken.None);
        resolved!.VersionNumber.Should().Be(2);
        resolved.ContentHash.Should().Be(changed.Hash);

        (await Definitions(store).SeedAsync([ShippedDefinition.FromParcelType(changed)], CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public void Seeding_passes_the_registrys_publish_entry_under_an_owner_role_principal()
    {
        DefinitionService.SeedPrincipal.Role.Should().Be(Role.Owner);
        CommandRegistry.Require("definition.publish", DefinitionService.SeedPrincipal).Should().NotBeNull();
    }

    [Fact]
    public async Task Parcels_and_the_type_list_use_a_projects_own_task_type_while_another_project_keeps_the_shipped_one()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var parcels = new ParcelService(store, TimeProvider.System);
        await PublishedProjectTypeAsync(Definitions(store), "alpha", QaTask with { EntryStates = ["refine", "qa"] });

        Parcel inAlpha = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "Starts in qa", "qa", CancellationToken.None);
        inAlpha.State.Should().Be("qa");
        inAlpha.TypeHash.Should().NotBe(ShippedTypes.Task.Hash);

        await FluentActions.Awaiting(() => parcels.AddAsync(Principal.LocalOwner, "beta", "task", "Not here", "qa", CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("*cannot start at 'qa'*");
        Parcel inBeta = await parcels.AddAsync(Principal.LocalOwner, "beta", "task", "Shipped", null, CancellationToken.None);
        inBeta.TypeHash.Should().Be(ShippedTypes.Task.Hash);

        IReadOnlyList<ParcelType> alphaTypes = await parcels.TypesAsync(Principal.LocalOwner, "alpha", CancellationToken.None);
        alphaTypes.Single(t => t.Name == "task").States.Should().Contain("qa");
        IReadOnlyList<ParcelType> betaTypes = await parcels.TypesAsync(Principal.LocalOwner, "beta", CancellationToken.None);
        betaTypes.Single(t => t.Name == "task").Hash.Should().Be(ShippedTypes.Task.Hash);
    }

    [Fact]
    public async Task On_a_fresh_node_the_type_list_and_parcel_lifecycle_match_the_shipped_types()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var parcels = new ParcelService(store, TimeProvider.System);

        IReadOnlyList<ParcelType> types = await parcels.TypesAsync(Principal.LocalOwner, "alpha", CancellationToken.None);
        types.Select(t => (t.Name, t.Hash)).Should().Equal(ShippedTypes.All.Select(t => (t.Name, t.Hash)));

        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, "alpha", "Task", "Lifecycle", null, CancellationToken.None);
        (await parcels.MoveAsync(Principal.LocalOwner, "alpha", parcel.Id, "build", CancellationToken.None)).State.Should().Be("build");
        (await parcels.CloseAsync(Principal.LocalOwner, "alpha", parcel.Id, "completed", "Done.", CancellationToken.None)).Resolution.Should().Be("completed");

        await FluentActions.Awaiting(() => parcels.AddAsync(Principal.LocalOwner, "alpha", "epic", "x", null, CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("'epic' is not an installed parcel type; installed: idea, task.");
    }

    [Fact]
    public async Task A_parcel_keeps_the_type_version_it_was_created_under_when_a_newer_one_is_published()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var parcels = new ParcelService(store, TimeProvider.System);
        Parcel inReview = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "Under way", null, CancellationToken.None);
        await parcels.MoveAsync(Principal.LocalOwner, "alpha", inReview.Id, "build", CancellationToken.None);
        await parcels.MoveAsync(Principal.LocalOwner, "alpha", inReview.Id, "review", CancellationToken.None);

        ParcelType withoutReview = ShippedTypes.Task with
        {
            States = ["refine", "build", ShippedTypes.Paused, ShippedTypes.Closed],
            Resolutions = ["shipped"],
            Moves = new Dictionary<string, IReadOnlyList<string>> { ["refine"] = ["build"] },
        };
        await PublishedProjectTypeAsync(Definitions(store), "alpha", withoutReview);

        Parcel moved = await parcels.MoveAsync(Principal.LocalOwner, "alpha", inReview.Id, "build", CancellationToken.None);
        moved.State.Should().Be("build");
        moved.TypeHash.Should().Be(ShippedTypes.Task.Hash);
        (await parcels.CloseAsync(Principal.LocalOwner, "alpha", inReview.Id, "completed", "Done.", CancellationToken.None))
            .Resolution.Should().Be("completed");

        Parcel fresh = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "New", null, CancellationToken.None);
        fresh.TypeHash.Should().Be(withoutReview.Hash);
        await FluentActions.Awaiting(() => parcels.CloseAsync(Principal.LocalOwner, "alpha", fresh.Id, "completed", "Done.", CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("*closes with one of shipped*");
    }

    [Fact]
    public async Task A_parcel_survives_a_differently_cased_type_name_and_the_retiring_of_its_version()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var parcels = new ParcelService(store, TimeProvider.System);
        DefinitionService definitions = Definitions(store);
        Parcel original = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "Before", null, CancellationToken.None);

        Definition cased = await PublishedProjectTypeAsync(definitions, "alpha", ShippedTypes.Task with { Name = "Task", EntryStates = ["refine", "build"] });
        (await parcels.MoveAsync(Principal.LocalOwner, "alpha", original.Id, "build", CancellationToken.None)).State.Should().Be("build");

        Parcel underCased = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "After", "build", CancellationToken.None);
        underCased.TypeName.Should().Be("Task");
        await definitions.RetireAsync(Principal.LocalOwner, "alpha", cased.Id, CancellationToken.None);

        (await parcels.MoveAsync(Principal.LocalOwner, "alpha", underCased.Id, "review", CancellationToken.None)).State.Should().Be("review");
        (await parcels.MoveAsync(Principal.LocalOwner, "alpha", original.Id, "review", CancellationToken.None)).State.Should().Be("review");
    }
}
