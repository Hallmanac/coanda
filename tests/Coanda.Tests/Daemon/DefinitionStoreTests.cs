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
        Stages = [.. ShippedTypes.Task.Stages, "qa"],
        Moves = new Dictionary<string, IReadOnlyList<string>>
        {
            ["refine"] = ["build"],
            ["build"] = ["review"],
            ["review"] = ["build", "qa"],
        },
        GateBindings = [.. ShippedTypes.Task.GateBindings, new GateBinding("review", "qa", RequiredFieldsGate.GateName)],
    };

    private static DefinitionService Definitions(IDocumentStore store) => new(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);

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
            Assert.NotNull(read);
            Assert.Equal(DefinitionKind.ParcelType, read.Kind);
            Assert.Equal("task", read.Name);
            Assert.Equal(DefinitionScope.Project, read.Scope);
            Assert.Equal("alpha", read.Project);
            Assert.Equal(1, read.VersionNumber);
            Assert.Equal(ParcelType.CanonicalJson(fork), read.Content);
            Assert.Equal(fork.Hash, read.ContentHash);
            Assert.Equal(DefinitionStatus.Published, read.Status);
            Assert.Equal(new ForkBase("task", 1), read.ForkBase);

            await using IQuerySession node = reopened.QuerySession(Tenants.Node);
            Definition? readOwner = await node.LoadAsync<Definition>(owner.Id, CancellationToken.None);
            Assert.NotNull(readOwner);
            Assert.Equal(DefinitionScope.Owner, readOwner.Scope);
            Assert.Null(readOwner.Project);
            Assert.Equal(DefinitionStatus.Draft, readOwner.Status);
            Assert.Null(readOwner.ForkBase);

            IReadOnlyList<Definition> shipped = [.. (await node.Query<Definition>().ToListAsync(CancellationToken.None)).Where(d => d.Scope == DefinitionScope.Shipped)];
            Assert.Equivalent(ShippedTypes.All.Select(t => (t.Name, t.Hash, 1, DefinitionStatus.Published)), shipped.Select(d => (d.Name, d.ContentHash, d.VersionNumber, d.Status)), strict: true);
        }
    }

    [Fact]
    public async Task An_owner_scope_draft_may_fork_a_seeded_shipped_version()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);

        Definition draft = await Definitions(store).DraftParcelTypeAsync(
            Principal.LocalOwner, DefinitionScope.Owner, null, QaTask, new ForkBase("task", 1), CancellationToken.None);

        Assert.Equal(DefinitionScope.Owner, draft.Scope);
        Assert.Equal(new ForkBase("task", 1), draft.ForkBase);
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
            DomainException exception = await Assert.ThrowsAsync<DomainException>(() => definitions.DraftParcelTypeAsync(
                    Principal.LocalOwner, scope, project, QaTask, unshipped, CancellationToken.None));
            Assert.StartsWith($"The fork base '{unshipped.Name}' version {unshipped.Version} of 'task' names no shipped parcel type", exception.Message, StringComparison.Ordinal);
        }

        // A shipped definition of another kind is not a fork base for a parcel type.
        await definitions.SeedAsync(ShippedDefinitions.Flows, CancellationToken.None);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => definitions.DraftParcelTypeAsync(
                Principal.LocalOwner, scope, project, QaTask, new ForkBase("build", 1), CancellationToken.None));
        Assert.StartsWith("The fork base 'build' version 1", exception2.Message, StringComparison.Ordinal);
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

        Assert.Equal(1, first.VersionNumber);
        Assert.Equal(2, second.VersionNumber);
        Assert.Equal(1, otherProject.VersionNumber);
        Assert.Equal(1, owner.VersionNumber);

        Definition? resolved = await resolver.ResolveAsync("alpha", DefinitionKind.ParcelType, "task", CancellationToken.None);
        Assert.NotNull(resolved);
        // an agent's drafts govern nothing
        Assert.Equal(DefinitionScope.Shipped, resolved.Scope);
        Assert.Equal(ShippedTypes.Task.Hash, resolved.ContentHash);
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

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => definitions.PublishAsync(principal, "alpha", draft.Id, CancellationToken.None));
        Assert.Contains($"not open to the {role} role", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => definitions.RetireAsync(principal, "alpha", draft.Id, CancellationToken.None));
        Assert.Contains($"not open to the {role} role", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publishing_and_retiring_change_what_resolution_returns()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        DefinitionService definitions = Definitions(store);
        var resolver = new DefinitionResolver(store);

        Definition published = await PublishedProjectTypeAsync(definitions, "alpha", QaTask);
        Assert.Equal(published.Id, (await resolver.ResolveAsync("alpha", DefinitionKind.ParcelType, "TASK", CancellationToken.None))!.Id);

        await definitions.RetireAsync(Principal.LocalOwner, "alpha", published.Id, CancellationToken.None);
        Definition? afterRetire = await resolver.ResolveAsync("alpha", DefinitionKind.ParcelType, "task", CancellationToken.None);
        Assert.Equal(DefinitionScope.Shipped, afterRetire!.Scope);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => definitions.PublishAsync(Principal.LocalOwner, "alpha", published.Id, CancellationToken.None));
        Assert.StartsWith("Only a draft can be published", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shipped_definition_cannot_be_retired_through_the_service()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var resolver = new DefinitionResolver(store);
        Definition shipped = (await resolver.ResolveAsync("alpha", DefinitionKind.ParcelType, "task", CancellationToken.None))!;

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => Definitions(store).RetireAsync(Principal.LocalOwner, null, shipped.Id, CancellationToken.None));
        Assert.StartsWith("A shipped definition cannot be retired", exception.Message, StringComparison.Ordinal);
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
            // a project tenant holds only its own definitions
            Assert.Empty(await project.Query<Definition>().ToListAsync(CancellationToken.None));
        }

        await using (IQuerySession node = store.QuerySession(Tenants.Node))
        {
            Assert.Equal(ShippedTypes.All.Count + 1, (await node.Query<Definition>().ToListAsync(CancellationToken.None)).Count);
        }

        foreach (string reserved in new[] { Tenants.Node, " _NODE " })
        {
            DomainException exception = await Assert.ThrowsAsync<DomainException>(() => parcels.ListAsync(Principal.LocalOwner, reserved, CancellationToken.None));
            Assert.Contains("reserved for node-level records", exception.Message, StringComparison.Ordinal);
            DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => parcels.AddAsync(Principal.LocalOwner, reserved, "task", "x", null, CancellationToken.None));
            Assert.Contains("reserved for node-level records", exception2.Message, StringComparison.Ordinal);
            DomainException exception3 = await Assert.ThrowsAsync<DomainException>(() => parcels.TypesAsync(Principal.LocalOwner, reserved, CancellationToken.None));
            Assert.Contains("reserved for node-level records", exception3.Message, StringComparison.Ordinal);
            DomainException exception4 = await Assert.ThrowsAsync<DomainException>(() => definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, reserved, QaTask, null, CancellationToken.None));
            Assert.Contains("reserved for node-level records", exception4.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Seeding_a_second_time_appends_no_events()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        long before = await CountEventsAsync(store, Tenants.Node);
        // each shipped type is a drafted and a published event
        Assert.Equal(ShippedTypes.All.Count * 2, before);

        IReadOnlyList<Definition> again = await Definitions(store).SeedAsync(ShippedDefinitions.ParcelTypes, CancellationToken.None);

        Assert.Empty(again);
        Assert.Equal(before, await CountEventsAsync(store, Tenants.Node));
    }

    [Fact]
    public async Task A_shipped_type_with_a_new_hash_is_seeded_as_the_next_version_and_resolution_returns_it()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        ParcelType changed = ShippedTypes.Task with { Resolutions = [.. ShippedTypes.Task.Resolutions, "wontfix"] };

        IReadOnlyList<Definition> seeded = await Definitions(store).SeedAsync([ShippedDefinition.FromParcelType(changed)], CancellationToken.None);

        Assert.Equal(2, Assert.Single(seeded).VersionNumber);
        Definition? resolved = await new DefinitionResolver(store).ResolveAsync("alpha", DefinitionKind.ParcelType, "task", CancellationToken.None);
        Assert.Equal(2, resolved!.VersionNumber);
        Assert.Equal(changed.Hash, resolved.ContentHash);

        Assert.Empty(await Definitions(store).SeedAsync([ShippedDefinition.FromParcelType(changed)], CancellationToken.None));
    }

    [Fact]
    public void Seeding_passes_the_registrys_publish_entry_under_an_owner_role_principal()
    {
        Assert.Equal(Role.Owner, DefinitionService.SeedPrincipal.Role);
        Assert.NotNull(CommandRegistry.Require("definition.publish", DefinitionService.SeedPrincipal));
    }

    [Fact]
    public async Task Parcels_and_the_type_list_use_a_projects_own_task_type_while_another_project_keeps_the_shipped_one()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, "alpha", CancellationToken.None);
        await RegisteredProjects.AddAsync(store, "beta", CancellationToken.None);
        var parcels = new ParcelService(store, TimeProvider.System);
        await PublishedProjectTypeAsync(Definitions(store), "alpha", QaTask with { EntryStages = ["refine", "qa"] });

        Parcel inAlpha = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "Starts in qa", "qa", CancellationToken.None);
        Assert.Equal("qa", inAlpha.Stage);
        Assert.NotEqual(ShippedTypes.Task.Hash, inAlpha.TypeHash);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => parcels.AddAsync(Principal.LocalOwner, "beta", "task", "Not here", "qa", CancellationToken.None));
        Assert.Contains("cannot start at 'qa'", exception.Message, StringComparison.Ordinal);
        Parcel inBeta = await parcels.AddAsync(Principal.LocalOwner, "beta", "task", "Shipped", null, CancellationToken.None);
        Assert.Equal(ShippedTypes.Task.Hash, inBeta.TypeHash);

        IReadOnlyList<ParcelType> alphaTypes = await parcels.TypesAsync(Principal.LocalOwner, "alpha", CancellationToken.None);
        Assert.Contains("qa", alphaTypes.Single(t => t.Name == "task").Stages);
        IReadOnlyList<ParcelType> betaTypes = await parcels.TypesAsync(Principal.LocalOwner, "beta", CancellationToken.None);
        Assert.Equal(ShippedTypes.Task.Hash, betaTypes.Single(t => t.Name == "task").Hash);
    }

    [Fact]
    public async Task On_a_fresh_node_the_type_list_and_parcel_lifecycle_match_the_shipped_types()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, "alpha", CancellationToken.None);
        var parcels = new ParcelService(store, TimeProvider.System);

        IReadOnlyList<ParcelType> types = await parcels.TypesAsync(Principal.LocalOwner, "alpha", CancellationToken.None);
        Assert.Equal(ShippedTypes.All.Select(t => (t.Name, t.Hash)), types.Select(t => (t.Name, t.Hash)));

        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, "alpha", "Task", "Lifecycle", null, CancellationToken.None);
        await parcels.BriefAsync(Principal.LocalOwner, "alpha", parcel.Id, cancellationToken: CancellationToken.None);
        Assert.Equal("build", (await parcels.MoveAsync(Principal.LocalOwner, "alpha", parcel.Id, "build", CancellationToken.None)).Stage);
        Assert.Equal("completed", (await parcels.CloseAsync(Principal.LocalOwner, "alpha", parcel.Id, "completed", "Done.", CancellationToken.None)).Resolution);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => parcels.AddAsync(Principal.LocalOwner, "alpha", "epic", "x", null, CancellationToken.None));
        Assert.Equal("'epic' is not an installed parcel type; installed: idea, task.", exception.Message);
    }

    [Fact]
    public async Task A_parcel_keeps_the_type_version_it_was_created_under_when_a_newer_one_is_published()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, "alpha", CancellationToken.None);
        var parcels = new ParcelService(store, TimeProvider.System);
        Parcel inReview = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "Under way", null, CancellationToken.None);
        await parcels.BriefAsync(Principal.LocalOwner, "alpha", inReview.Id, cancellationToken: CancellationToken.None);
        await parcels.MoveAsync(Principal.LocalOwner, "alpha", inReview.Id, "build", CancellationToken.None);
        await parcels.MoveAsync(Principal.LocalOwner, "alpha", inReview.Id, "review", CancellationToken.None);

        ParcelType withoutReview = ShippedTypes.Task with
        {
            Stages = ["refine", "build", ShippedTypes.Paused, ShippedTypes.Closed],
            Resolutions = ["shipped"],
            Moves = new Dictionary<string, IReadOnlyList<string>> { ["refine"] = ["build"] },
            GateBindings = [new GateBinding("refine", "build", RequiredFieldsGate.GateName)],
        };
        await PublishedProjectTypeAsync(Definitions(store), "alpha", withoutReview);

        Parcel moved = await parcels.MoveAsync(Principal.LocalOwner, "alpha", inReview.Id, "build", CancellationToken.None);
        Assert.Equal("build", moved.Stage);
        Assert.Equal(ShippedTypes.Task.Hash, moved.TypeHash);
        Assert.Equal("completed", (await parcels.CloseAsync(Principal.LocalOwner, "alpha", inReview.Id, "completed", "Done.", CancellationToken.None))
            .Resolution);

        Parcel fresh = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "New", null, CancellationToken.None);
        Assert.Equal(withoutReview.Hash, fresh.TypeHash);
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => parcels.CloseAsync(Principal.LocalOwner, "alpha", fresh.Id, "completed", "Done.", CancellationToken.None));
        Assert.Contains("closes with one of shipped", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parcel_survives_a_differently_cased_type_name_and_the_retiring_of_its_version()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, "alpha", CancellationToken.None);
        var parcels = new ParcelService(store, TimeProvider.System);
        DefinitionService definitions = Definitions(store);
        Parcel original = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "Before", null, CancellationToken.None);
        await parcels.BriefAsync(Principal.LocalOwner, "alpha", original.Id, cancellationToken: CancellationToken.None);

        Definition cased = await PublishedProjectTypeAsync(definitions, "alpha", ShippedTypes.Task with { Name = "Task", EntryStages = ["refine", "build"] });
        Assert.Equal("build", (await parcels.MoveAsync(Principal.LocalOwner, "alpha", original.Id, "build", CancellationToken.None)).Stage);

        Parcel underCased = await parcels.AddAsync(Principal.LocalOwner, "alpha", "task", "After", "build", CancellationToken.None);
        Assert.Equal("Task", underCased.TypeName);
        await parcels.BriefAsync(Principal.LocalOwner, "alpha", underCased.Id, cancellationToken: CancellationToken.None);
        await definitions.RetireAsync(Principal.LocalOwner, "alpha", cased.Id, CancellationToken.None);

        Assert.Equal("review", (await parcels.MoveAsync(Principal.LocalOwner, "alpha", underCased.Id, "review", CancellationToken.None)).Stage);
        Assert.Equal("review", (await parcels.MoveAsync(Principal.LocalOwner, "alpha", original.Id, "review", CancellationToken.None)).Stage);
    }
}
