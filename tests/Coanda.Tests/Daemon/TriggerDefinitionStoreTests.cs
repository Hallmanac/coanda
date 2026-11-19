using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Coanda.Tests.Core;
using Fisher;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A trigger definition through the definition service and the resolver: drafted and published in
/// project and owner scope, retired, forked from a module's trigger, and resolved nearest scope first.
/// </summary>
public sealed class TriggerDefinitionStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly ModuleArtifact Tracker = ModuleArtifact.Of(
        "tracker", "1.0.0", [ModuleDefinition.FromTrigger(SampleTriggers.IssueOpened), ModuleDefinition.FromTrigger(SampleTriggers.ReviewRequested)]);

    /// <summary>A definition service on <paramref name="store"/>, with the tracker module's triggers imported.</summary>
    private static async Task<DefinitionService> ImportedAsync(IDocumentStore store)
    {
        var definitions = new DefinitionService(store, TimeProvider.System);
        await definitions.ImportAsync(Tracker, None);
        return definitions;
    }

    private static Task<Definition> DraftAsync(DefinitionService definitions, DefinitionScope scope, string? project, TriggerDefinition trigger, ForkBase? forkBase = null) =>
        definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Trigger, scope, project, trigger.Name, TriggerDefinition.CanonicalJson(trigger), trigger.Hash, forkBase, None);

    private static async Task<Definition> PublishedAsync(DefinitionService definitions, DefinitionScope scope, string? project, TriggerDefinition trigger, ForkBase? forkBase = null)
    {
        Definition draft = await DraftAsync(definitions, scope, project, trigger, forkBase);
        return await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, None);
    }

    [Theory]
    [InlineData(DefinitionScope.Project, "alpha")]
    [InlineData(DefinitionScope.Owner, null)]
    public async Task A_trigger_is_drafted_published_and_retired_through_the_service(DefinitionScope scope, string? project)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        DefinitionService definitions = await ImportedAsync(store);
        TriggerDefinition trigger = SampleTriggers.EvaluateBuild;

        Definition published = await PublishedAsync(definitions, scope, project, trigger);
        Assert.Equal((DefinitionKind.Trigger, scope, project, DefinitionStatus.Published, trigger.Hash), (published.Kind, published.Scope, published.Project, published.Status, published.ContentHash));

        var resolver = new DefinitionResolver(store);
        Assert.Equal(published.Id, (await resolver.ResolveAsync("alpha", DefinitionKind.Trigger, "EVALUATE-BUILD", None))?.Id);

        Definition retired = await definitions.RetireAsync(Principal.LocalOwner, project, published.Id, None);
        Assert.Equal(DefinitionStatus.Retired, retired.Status);
        Assert.Null(await resolver.ResolveAsync("alpha", DefinitionKind.Trigger, "evaluate-build", None));
    }

    [Fact]
    public async Task A_trigger_is_drafted_as_a_fork_of_a_modules_trigger_and_a_fork_base_naming_none_is_refused_calling_it_a_trigger()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        DefinitionService definitions = await ImportedAsync(store);
        var forkBase = new ForkBase("review-requested", "tracker", "1.0.0", SampleTriggers.ReviewRequested.Hash);
        TriggerDefinition fork = SampleTriggers.ReviewRequested with { Filters = SampleTriggers.Filters(("reviewer", "sam")) };

        Definition draft = await DraftAsync(definitions, DefinitionScope.Project, "alpha", fork, forkBase);
        Assert.Equal(forkBase, draft.ForkBase);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            DraftAsync(definitions, DefinitionScope.Owner, null, fork, forkBase with { ContentHash = fork.Hash }));
        Assert.Contains("names no module-scope trigger;", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_projects_own_trigger_wins_over_an_owners_and_a_modules_of_the_same_name_and_resolving_all_gives_one_per_name()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        DefinitionService definitions = await ImportedAsync(store);
        var resolver = new DefinitionResolver(store);
        TriggerDefinition owners = SampleTriggers.IssueOpened with { HourlyCap = 3 };
        TriggerDefinition alphas = SampleTriggers.IssueOpened with { Name = "ISSUE-OPENED", HourlyCap = 2 };

        Definition owner = await PublishedAsync(definitions, DefinitionScope.Owner, null, owners);
        Definition alpha = await PublishedAsync(definitions, DefinitionScope.Project, "alpha", alphas);

        Assert.Equal(alpha.Id, (await resolver.ResolveAsync("alpha", DefinitionKind.Trigger, "issue-opened", None))?.Id);
        Assert.Equal(owner.Id, (await resolver.ResolveAsync("beta", DefinitionKind.Trigger, "issue-opened", None))?.Id);
        Definition? shipped = await resolver.ResolveAsync("beta", DefinitionKind.Trigger, "review-requested", None);
        Assert.Equal((DefinitionScope.Module, "tracker"), (shipped?.Scope, shipped?.Module));

        IReadOnlyList<Definition> all = await resolver.ResolveAllAsync("alpha", DefinitionKind.Trigger, None);
        Assert.Equal(
            new[] { (DefinitionScope.Project, alphas.Hash), (DefinitionScope.Module, SampleTriggers.ReviewRequested.Hash) },
            all.Select(d => (d.Scope, d.ContentHash)));

        // A trigger is resolved among triggers only: a flow or an agent of the same name is not one.
        Assert.Empty(await resolver.ResolveAllAsync("alpha", DefinitionKind.Agent, None));
    }

    [Fact]
    public async Task Importing_the_same_triggers_a_second_time_records_nothing()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        DefinitionService definitions = await ImportedAsync(store);

        Assert.Empty(await definitions.ImportAsync(Tracker, None));
    }

    [Fact]
    public async Task A_modules_trigger_that_breaks_a_rule_is_refused_at_import_calling_it_a_trigger_and_nothing_is_recorded()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var definitions = new DefinitionService(store, TimeProvider.System);
        TriggerDefinition capped = SampleTriggers.ReviewRequested with { HourlyCap = 1 };
        ModuleArtifact broken = ModuleArtifact.Of("tracker", "1.0.0", [ModuleDefinition.FromTrigger(SampleTriggers.IssueOpened), ModuleDefinition.FromTrigger(capped)]);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => definitions.ImportAsync(broken, None));

        Assert.Equal(
            "Module 'tracker' version 1.0.0 cannot be imported: its trigger 'review-requested' breaks a rule: Trigger 'review-requested' acts on the parcel the event names and carries the hourly cap 1; it creates no parcel for a cap to count, so it carries none.",
            exception.Message);
        Assert.Empty(await new DefinitionResolver(store).ResolveAllAsync("alpha", DefinitionKind.Trigger, None));
    }
}
