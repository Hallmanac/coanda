using System.Text.Json;
using Coanda.Core.Modules.Events;
using Coanda.Core.Modules;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Projects.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Core;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Fisher;
using JasperFx.Events.Documents;
using JasperFx.Events;

namespace Coanda.Tests.Daemon;

public sealed class ProjectServiceTests
{
    private const string Remote = "git@github.com:you/coanda.git";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    [Fact]
    public async Task An_added_project_shows_and_lists_with_its_repo_and_survives_reopening_the_store()
    {
        using var temp = new TemporaryHome();
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath))
        {
            Project added = await new ProjectService(store, TimeProvider.System).AddAsync(Owner, " coanda ", Remote, [], None);
            Assert.Equal("coanda", added.Name);
            Assert.Equal(Owner.Id, added.RegisteredBy);
        }

        await using IDocumentStore reopened = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(reopened, TimeProvider.System);

        Project? shown = await projects.ShowAsync(Owner, "coanda", None);
        Assert.NotNull(shown);
        Assert.Equal("coanda", shown.Name);
        Assert.Equal([new ProjectRepo("coanda", Remote)], shown.Repos);

        // names are compared after trimming
        Assert.NotNull(await projects.ShowAsync(Owner, "  coanda", None));
        // no case folding is applied
        Assert.Null(await projects.ShowAsync(Owner, "Coanda", None));
        Assert.Null(await projects.ShowAsync(Owner, "other", None));
    }

    [Fact]
    public async Task A_project_whose_name_differs_from_a_registered_one_only_in_letter_case_is_refused_and_not_recorded()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        await projects.AddAsync(Owner, "coanda", "https://example.invalid/a.git", [], None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => projects.AddAsync(Owner, "Coanda", "https://example.invalid/b.git", [], None));
        Assert.StartsWith("Project 'Coanda' differs from the registered project 'coanda' only in letter case", exception.Message, StringComparison.Ordinal);

        Assert.Equal(["coanda"], (await projects.ListAsync(Owner, None)).Select(p => p.Name));
    }

    [Fact]
    public async Task The_list_holds_every_project_on_the_node_ordered_by_name_with_ordinal_comparison()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        await projects.AddAsync(Owner, "beta", "https://example.invalid/beta.git", [], None);
        await projects.AddAsync(Owner, "alpha", "https://example.invalid/alpha.git", [], None);
        await projects.AddAsync(Owner, "Gamma", "https://example.invalid/gamma.git", [], None);

        IReadOnlyList<Project> listed = await projects.ListAsync(Owner, None);

        Assert.Equal(["Gamma", "alpha", "beta"], listed.Select(p => p.Name));
        Assert.Single(listed, p => p.Name == "beta" && p.Repos.SequenceEqual([new ProjectRepo("beta", "https://example.invalid/beta.git")]));
    }

    [Fact]
    public async Task A_second_add_of_a_name_with_a_record_is_refused_even_when_it_differs_only_by_surrounding_space()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        await projects.AddAsync(Owner, "coanda", Remote, [], None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => projects.AddAsync(Owner, " coanda ", "https://example.invalid/other.git", [], None));
        Assert.StartsWith("Project 'coanda' is already registered", exception.Message, StringComparison.Ordinal);

        Assert.Equal(Remote, Assert.Single(Assert.Single(await projects.ListAsync(Owner, None)).Repos).Remote);
    }

    [Fact]
    public async Task Two_concurrent_adds_of_one_name_leave_one_record()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);

        Task<Project>[] adds =
        [
            projects.AddAsync(Owner, "coanda", "https://example.invalid/a.git", [], None),
            projects.AddAsync(Owner, "coanda", "https://example.invalid/b.git", [], None),
        ];
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => Task.WhenAll(adds));
        Assert.Contains("already registered", exception.Message, StringComparison.Ordinal);

        Assert.Equal(1, adds.Count(a => a.IsCompletedSuccessfully));
        Assert.Single(await projects.ListAsync(Owner, None));
    }

    [Theory]
    [InlineData("", Remote, "A project name is required*")]
    [InlineData("_node", Remote, "*reserved for node-level records*")]
    [InlineData("_NODE", Remote, "*reserved for node-level records*")]
    [InlineData("coanda", " ", "*needs the remote of a repo*")]
    public async Task A_blank_name_the_reserved_name_or_a_blank_remote_is_refused(string name, string remote, string rule)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => projects.AddAsync(Owner, name, remote, [], None));
        Assert.Matches(WildcardPattern.ToRegex(rule), exception.Message);
        Assert.Empty(await projects.ListAsync(Owner, None));
    }

    [Fact]
    public async Task A_registration_records_its_module_settings_in_the_same_save_as_the_project()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);

        Project added = await projects.AddAsync(
            Owner,
            "coanda",
            Remote,
            [
                RegisteredProjects.Setting(SoftwareSettings.PrimaryBranch, new Dictionary<string, string> { ["coanda"] = "trunk" }),
                RegisteredProjects.Setting(SoftwareSettings.Build, "make"),
                RegisteredProjects.Setting(SoftwareSettings.Test, "make test"),
            ],
            None);

        await using IQuerySession session = store.QuerySession("coanda");
        ModuleSettings settings = Assert.Single(await session.Query<ModuleSettings>().ToListAsync(None));
        Assert.Equal(SoftwareSteps.Module, settings.Module);
        Assert.Equal(
            [(SoftwareSettings.Build, "\"make\""), (SoftwareSettings.PrimaryBranch, """{"coanda":"trunk"}"""), (SoftwareSettings.Test, "\"make test\"")],
            settings.Values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => (v.Key, v.Value)));

        // One save: the project's stream and the settings' stream are written together, at one sequence.
        IReadOnlyList<IEvent> projectEvents = await session.Events.FetchStreamAsync(added.Id, token: None);
        IReadOnlyList<IEvent> settingEvents = await session.Events.FetchStreamAsync(settings.Id, token: None);
        Assert.Equal([typeof(ProjectRegistered), typeof(ProjectModulesSet)], projectEvents.Select(e => e.Data.GetType()));
        Assert.Equal(3, settingEvents.Count);
        Assert.All(settingEvents, e => Assert.Equal(Owner.Id, ((ModuleSettingSet)e.Data).SetBy));
        Assert.Equal(projectEvents[^1].Sequence + 1, settingEvents[0].Sequence);
    }

    public static TheoryData<string, ModuleSettingValue[], string> RefusedSettings => new()
    {
        {
            "a blank build command",
            [RegisteredProjects.Setting(SoftwareSettings.PrimaryBranch, new Dictionary<string, string> { ["coanda"] = "main" }), RegisteredProjects.Setting(SoftwareSettings.Build, " ")],
            "The value given for setting 'build' of module 'software' does not meet the setting's schema: PatternMismatch at #/."
        },
        {
            "a blank test command",
            [RegisteredProjects.Setting(SoftwareSettings.Test, "")],
            "The value given for setting 'test' of module 'software' does not meet the setting's schema: PatternMismatch at #/."
        },
        {
            "a setting the module does not declare",
            [RegisteredProjects.Setting("colour", "red")],
            "Module 'software' declares no replicated setting named 'colour'; it declares 'primary-branch', 'build', 'test', 'required-checks'."
        },
        {
            "a module the registration does not switch on",
            [new ModuleSettingValue("mail", "channel", JsonSerializer.SerializeToElement("#builds"))],
            "Project 'coanda' was given a setting of module 'mail', which its registration does not switch on; a registration records the settings of the modules it switches on, 'software'."
        },
        {
            "a setting given twice",
            [RegisteredProjects.Setting(SoftwareSettings.Build, "make"), RegisteredProjects.Setting(SoftwareSettings.Build, "ninja")],
            "Project 'coanda' was given setting 'build' of module 'software' more than once; give each setting once."
        },
    };

    [Theory]
    [MemberData(nameof(RefusedSettings))]
    public async Task A_registration_whose_settings_are_refused_registers_nothing(string refused, ModuleSettingValue[] settings, string rule)
    {
        Assert.NotEmpty(refused);
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => projects.AddAsync(Owner, "coanda", Remote, settings, None));

        Assert.Equal(rule, exception.Message);
        Assert.Empty(await projects.ListAsync(Owner, None));
        await using IQuerySession session = store.QuerySession("coanda");
        Assert.Empty(await session.Query<ModuleSettings>().ToListAsync(None));
    }

    [Fact]
    public async Task Adding_a_project_is_refused_to_an_agent_and_showing_or_listing_is_open_to_one()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        var agent = new Principal("build-session", Role.Agent);
        var member = new Principal("teammate", Role.Member);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => projects.AddAsync(agent, "coanda", Remote, [], None));
        Assert.Equal("'project.add' is not open to the Agent role.", exception.Message);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => projects.AddAsync(member, "coanda", Remote, [], None));
        Assert.Equal("'project.add' is not open to the Member role.", exception2.Message);

        await projects.AddAsync(Owner, "coanda", Remote, [], None);
        Assert.NotNull(await projects.ShowAsync(agent, "coanda", None));
        Assert.Single(await projects.ListAsync(agent, None));
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

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => parcels.AddAsync(Owner, " beta ", type, "Nowhere to go", null, None));
        Assert.StartsWith("Project 'beta' is not registered; register it with coa project add beta", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await parcels.ListAsync(Owner, "beta", None));

        Assert.Equal("alpha", (await parcels.AddAsync(Owner, "alpha", "task", "Somewhere to go", null, None)).Project);
    }

    [Fact]
    public async Task A_blank_or_reserved_project_name_keeps_its_own_refusal_on_a_parcel_add()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var parcels = new ParcelService(store, TimeProvider.System);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => parcels.AddAsync(Owner, " ", "task", "x", null, None));
        Assert.StartsWith("A project name is required", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => parcels.AddAsync(Owner, Tenants.Node, "task", "x", null, None));
        Assert.Contains("reserved for node-level records", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parcel_an_unregistered_project_already_holds_still_moves_and_closes()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var parcels = new ParcelService(store, TimeProvider.System);

        // Written straight to the store, as a parcel created before projects had records was.
        ParcelCreated created = ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "legacy", "From before records", null, Owner.Id, DateTimeOffset.UtcNow);
        await using (IDocumentSession session = store.LightweightSession("legacy"))
        {
            session.Events.StartStream<Parcel>(created.ParcelId, created);
            await session.SaveChangesAsync(None);
        }

        await parcels.BriefAsync(Owner, "legacy", created.ParcelId, cancellationToken: None);
        Assert.Equal("build", (await parcels.MoveAsync(Owner, "legacy", created.ParcelId, "build", None)).Stage);
        Assert.Equal("abandoned", (await parcels.CloseAsync(Owner, "legacy", created.ParcelId, "abandoned", "Never registered.", None)).Resolution);
    }
}
