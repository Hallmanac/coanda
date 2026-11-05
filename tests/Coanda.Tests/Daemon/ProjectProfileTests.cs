using Coanda.Cli;
using Coanda.Core.Projects.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Core;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Api;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;
using JasperFx.Events;

namespace Coanda.Tests.Daemon;

public sealed class ProjectProfileTests
{
    private const string Project = "coanda";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    private static readonly CredentialProfiles Held = new(
    [
        new CredentialProfile("claude-code", "work", "/home/me/.claude-work", null),
        new CredentialProfile("claude-code", "ci", null, "op read op://ci/key"),
    ]);

    [Fact]
    public async Task A_set_profile_is_shown_and_a_later_one_replaces_it_in_the_node_tenant_only()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System, Held, ModuleSettingsSchemas.None);
        await RegisteredProjects.AddAsync(store, Project, None);

        // a project names no profile until one is set
        Assert.Null(await projects.ProfileOfAsync(Owner, Project, None));

        ProjectProfile first = await projects.SetProfileAsync(Owner, $" {Project} ", " work ", None);
        // names are compared and recorded after trimming
        Assert.Equal("work", first.Profile);
        Assert.Equal(Project, first.Project);
        Assert.Equal("work", await projects.ProfileOfAsync(Owner, Project, None));

        ProjectProfile second = await projects.SetProfileAsync(Owner, Project, "ci", None);
        // a project has one setting, which a later call replaces
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("ci", await projects.ProfileOfAsync(Owner, Project, None));
        Assert.Equal(new Dictionary<string, string> { [Project] = "ci" }, await projects.ProfilesAsync(Owner, None));

        await using IQuerySession node = store.QuerySession(Tenants.Node);
        IReadOnlyList<IEvent> events = await node.Events.FetchStreamAsync(first.Id, token: None);
        Assert.Equal(["work", "ci"], events.Select(e => e.Data).OfType<ProjectProfileSet>().Select(e => e.Profile));

        await using IQuerySession own = store.QuerySession(Project);
        IReadOnlyList<ProjectProfile> ownSettings = await own.Query<ProjectProfile>().ToListAsync(None);
        // nothing in the project's own tenant carries the setting
        Assert.Empty(ownSettings);
        IReadOnlyList<IEvent> ownStream = await own.Events.FetchStreamAsync(first.Id, token: None);
        Assert.Empty(ownStream);
    }

    [Fact]
    public async Task Set_profile_is_refused_to_an_agent_principal()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System, Held, ModuleSettingsSchemas.None);
        await RegisteredProjects.AddAsync(store, Project, None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => projects.SetProfileAsync(new Principal("flow-run:x", Role.Agent), Project, "work", None));
        Assert.Equal("'project.set-profile' is not open to the Agent role.", exception.Message);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => projects.SetProfileAsync(new Principal("someone", Role.Member), Project, "work", None));
        Assert.Equal("'project.set-profile' is not open to the Member role.", exception2.Message);
        Assert.Null(await projects.ProfileOfAsync(Owner, Project, None));
    }

    [Fact]
    public async Task Set_profile_is_refused_for_a_project_with_no_record()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System, Held, ModuleSettingsSchemas.None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => projects.SetProfileAsync(Owner, "nowhere", "work", None));
        Assert.Equal(ProjectService.Unregistered("nowhere"), exception.Message);
        Assert.Contains("coa project add", ProjectService.Unregistered("nowhere"), StringComparison.Ordinal);
        Assert.Null(await projects.ProfileOfAsync(Owner, "nowhere", None));
    }

    [Theory]
    [InlineData("personal")]
    [InlineData("Work")]
    [InlineData(" ")]
    public async Task Set_profile_is_refused_for_a_profile_the_configuration_does_not_hold(string profile)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System, Held, ModuleSettingsSchemas.None);
        await RegisteredProjects.AddAsync(store, Project, None);

        await Assert.ThrowsAsync<DomainException>(() => projects.SetProfileAsync(Owner, Project, profile, None));
        Assert.Null(await projects.ProfileOfAsync(Owner, Project, None));
    }

    [Fact]
    public async Task The_refusal_of_an_unknown_profile_names_it_and_the_profiles_held()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => new ProjectService(store, TimeProvider.System, Held, ModuleSettingsSchemas.None).SetProfileAsync(Owner, Project, "personal", None));
        Assert.Equal("The running daemon's node configuration holds no credential profile named 'personal', so project 'coanda' cannot name it; the configuration holds 'ci', 'work'.", exception.Message);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => new ProjectService(store, TimeProvider.System).SetProfileAsync(Owner, Project, "work", None));
        Assert.EndsWith("the configuration holds no credential profiles.", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cli_client_sets_a_profile_and_shows_it_through_the_running_daemon()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Profiles = Held.All }, None);
        using var client = new DaemonClient(temp.Home);
        await RegisteredProjects.AddAsync(daemon, Project, None);

        Assert.Null((await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf(Project), None)).Profile);

        ProjectView set = await client.PostAsync<ProjectView>("/projects/profile", new ProjectSetProfileRequest(Project, "work"), None);
        Assert.Equal("work", set.Profile);
        Assert.Equal("work", (await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf(Project), None)).Profile);
        Assert.Equal("work", Assert.Single(await client.GetAsync<List<ProjectView>>("/projects", None)).Profile);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ProjectView>("/projects/profile", new ProjectSetProfileRequest(Project, "personal"), None));
        Assert.Contains("holds no credential profile named 'personal'", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ProjectView>("/projects/profile", new ProjectSetProfileRequest("nowhere", "work"), None));
        Assert.Contains("coa project add", exception2.Message, StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }
}
