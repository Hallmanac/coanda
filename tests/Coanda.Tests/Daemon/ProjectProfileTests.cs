using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;

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
        var projects = new ProjectService(store, TimeProvider.System, Held);
        await RegisteredProjects.AddAsync(store, Project, None);

        (await projects.ProfileOfAsync(Owner, Project, None)).Should().BeNull("a project names no profile until one is set");

        ProjectProfile first = await projects.SetProfileAsync(Owner, $" {Project} ", " work ", None);
        first.Profile.Should().Be("work", "names are compared and recorded after trimming");
        first.Project.Should().Be(Project);
        (await projects.ProfileOfAsync(Owner, Project, None)).Should().Be("work");

        ProjectProfile second = await projects.SetProfileAsync(Owner, Project, "ci", None);
        second.Id.Should().Be(first.Id, "a project has one setting, which a later call replaces");
        (await projects.ProfileOfAsync(Owner, Project, None)).Should().Be("ci");
        (await projects.ProfilesAsync(Owner, None)).Should().Equal(new Dictionary<string, string> { [Project] = "ci" });

        await using IQuerySession node = store.QuerySession(Tenants.Node);
        IReadOnlyList<IEvent> events = await node.Events.FetchStreamAsync(first.Id, token: None);
        events.Select(e => e.Data).OfType<ProjectProfileSet>().Select(e => e.Profile).Should().Equal("work", "ci");

        await using IQuerySession own = store.QuerySession(Project);
        IReadOnlyList<ProjectProfile> ownSettings = await own.Query<ProjectProfile>().ToListAsync(None);
        ownSettings.Should().BeEmpty("nothing in the project's own tenant carries the setting");
        IReadOnlyList<IEvent> ownStream = await own.Events.FetchStreamAsync(first.Id, token: None);
        ownStream.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_profile_is_refused_to_an_agent_principal()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System, Held);
        await RegisteredProjects.AddAsync(store, Project, None);

        await FluentActions.Awaiting(() => projects.SetProfileAsync(new Principal("flow:x", Role.Agent), Project, "work", None))
            .Should().ThrowAsync<DomainException>().WithMessage("'project.set-profile' is not open to the Agent role.");
        await FluentActions.Awaiting(() => projects.SetProfileAsync(new Principal("someone", Role.Member), Project, "work", None))
            .Should().ThrowAsync<DomainException>().WithMessage("'project.set-profile' is not open to the Member role.");
        (await projects.ProfileOfAsync(Owner, Project, None)).Should().BeNull();
    }

    [Fact]
    public async Task Set_profile_is_refused_for_a_project_with_no_record()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System, Held);

        await FluentActions.Awaiting(() => projects.SetProfileAsync(Owner, "nowhere", "work", None))
            .Should().ThrowAsync<DomainException>().WithMessage(ProjectService.Unregistered("nowhere"));
        ProjectService.Unregistered("nowhere").Should().Contain("coa project add");
        (await projects.ProfileOfAsync(Owner, "nowhere", None)).Should().BeNull();
    }

    [Theory]
    [InlineData("personal")]
    [InlineData("Work")]
    [InlineData(" ")]
    public async Task Set_profile_is_refused_for_a_profile_the_configuration_does_not_hold(string profile)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System, Held);
        await RegisteredProjects.AddAsync(store, Project, None);

        await FluentActions.Awaiting(() => projects.SetProfileAsync(Owner, Project, profile, None))
            .Should().ThrowAsync<DomainException>();
        (await projects.ProfileOfAsync(Owner, Project, None)).Should().BeNull();
    }

    [Fact]
    public async Task The_refusal_of_an_unknown_profile_names_it_and_the_profiles_held()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);

        await FluentActions.Awaiting(() => new ProjectService(store, TimeProvider.System, Held).SetProfileAsync(Owner, Project, "personal", None))
            .Should().ThrowAsync<DomainException>()
            .WithMessage("The running daemon's node configuration holds no credential profile named 'personal', so project 'coanda' cannot name it; the configuration holds 'ci', 'work'.");
        await FluentActions.Awaiting(() => new ProjectService(store, TimeProvider.System).SetProfileAsync(Owner, Project, "work", None))
            .Should().ThrowAsync<DomainException>().WithMessage("*the configuration holds no credential profiles.");
    }

    [Fact]
    public async Task The_cli_client_sets_a_profile_and_shows_it_through_the_running_daemon()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Profiles = Held.All }, None);
        using var client = new DaemonClient(temp.Home);
        await RegisteredProjects.AddAsync(daemon, Project, None);

        (await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf(Project), None)).Profile.Should().BeNull();

        ProjectView set = await client.PostAsync<ProjectView>("/projects/profile", new ProjectSetProfileRequest(Project, "work"), None);
        set.Profile.Should().Be("work");
        (await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf(Project), None)).Profile.Should().Be("work");
        (await client.GetAsync<List<ProjectView>>("/projects", None)).Should().ContainSingle().Which.Profile.Should().Be("work");

        await FluentActions.Awaiting(() => client.PostAsync<ProjectView>("/projects/profile", new ProjectSetProfileRequest(Project, "personal"), None))
            .Should().ThrowAsync<DomainException>().WithMessage("*holds no credential profile named 'personal'*");
        await FluentActions.Awaiting(() => client.PostAsync<ProjectView>("/projects/profile", new ProjectSetProfileRequest("nowhere", "work"), None))
            .Should().ThrowAsync<DomainException>().WithMessage("*coa project add*");

        await daemon.StopAsync(None);
    }
}
