using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Projects;
using Fisher;

namespace Coanda.Tests.Daemon;

/// <summary>The checks a project's record names for a merge to wait for: set, replaced, refused, and shown.</summary>
public sealed class ProjectRequiredChecksTests
{
    private const string Remote = "git@github.com:you/coanda.git";
    private const string Ubuntu = "build-test (ubuntu-latest)";
    private const string Windows = "build-test (windows-latest)";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    [Fact]
    public async Task A_project_names_no_check_until_set_and_a_set_replaces_the_list_with_the_checks_given_exactly_as_given()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        (await projects.AddAsync(Owner, "coanda", Remote, "main", None)).RequiredChecks.Should().BeEmpty();

        Project set = await projects.SetRequiredChecksAsync(Owner, "coanda", [Ubuntu, Windows], None);
        set.RequiredChecks.Should().Equal(Ubuntu, Windows);
        (await projects.ShowAsync(Owner, "coanda", None))?.RequiredChecks.Should().Equal(Ubuntu, Windows);

        await projects.SetRequiredChecksAsync(Owner, "coanda", [" lint "], None);
        (await projects.ShowAsync(Owner, "coanda", None))?.RequiredChecks.Should().Equal([" lint "], "names are stored exactly as given and the list is replaced");
        (await projects.ShowAsync(Owner, "coanda", None))?.Remote.Should().Be(Remote, "setting checks changes nothing else on the record");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_check_name_is_refused_and_nothing_is_recorded(string blank)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        await projects.AddAsync(Owner, "coanda", Remote, "main", None);
        await projects.SetRequiredChecksAsync(Owner, "coanda", [Ubuntu], None);

        await FluentActions.Awaiting(() => projects.SetRequiredChecksAsync(Owner, "coanda", [Windows, blank], None))
            .Should().ThrowAsync<DomainException>().WithMessage("Project 'coanda' was given a blank check name*");
        await FluentActions.Awaiting(() => projects.SetRequiredChecksAsync(Owner, "coanda", [], None))
            .Should().ThrowAsync<DomainException>().WithMessage("Project 'coanda' needs at least one check*");

        (await projects.ShowAsync(Owner, "coanda", None))?.RequiredChecks.Should().Equal(Ubuntu);
    }

    [Fact]
    public async Task A_project_with_no_record_is_refused()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);

        await FluentActions.Awaiting(() => new ProjectService(store, TimeProvider.System).SetRequiredChecksAsync(Owner, "nowhere", [Ubuntu], None))
            .Should().ThrowAsync<DomainException>().WithMessage(ProjectDecider.Unregistered("nowhere"));
    }

    [Theory]
    [InlineData(Role.Agent)]
    [InlineData(Role.Member)]
    public async Task The_set_is_open_to_the_owner_only(Role role)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var projects = new ProjectService(store, TimeProvider.System);
        await projects.AddAsync(Owner, "coanda", Remote, "main", None);

        await FluentActions.Awaiting(() => projects.SetRequiredChecksAsync(new Principal("someone", role), "coanda", [Ubuntu], None))
            .Should().ThrowAsync<DomainException>().WithMessage($"'project.set' is not open to the {role} role.");

        (await projects.ShowAsync(Owner, "coanda", None))?.RequiredChecks.Should().BeEmpty();
        CommandRegistry.Entries.Single(e => e.Name == "project.set").AllowedRoles.Should().BeEquivalentTo([Role.Owner]);
    }

    [Fact]
    public async Task The_endpoint_sets_the_checks_and_coa_project_show_prints_them()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await client.PostAsync<ProjectView>("/projects", new ProjectAddRequest("team/coanda", Remote, "main"), None);
        ProjectView added = await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf("team/coanda"), None);
        ProjectShowCommand.ShowRows(added).Should().Contain(row => row.Key == "Required checks" && row.Value.StartsWith("none set", StringComparison.Ordinal));

        var settings = new ProjectSetSettings { Name = "team/coanda", RequiredChecks = [Ubuntu, "lint [fast]"] };
        ProjectView set = await client.PutAsync<ProjectView>(ProjectEndpoints.RequiredChecksPathOf(settings.Name), settings.ToRequest(), None);

        ProjectEndpoints.RequiredChecksPathOf("team/coanda").Should().Be("/projects/team/coanda/required-checks");
        set.RequiredChecks.Should().Equal(Ubuntu, "lint [fast]");
        ProjectView shown = await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf("team/coanda"), None);
        ProjectShowCommand.ShowRows(shown).Should().Contain(("Required checks", $"{Ubuntu}, lint [[fast]]"), "the checks are printed as text, not markup");

        await FluentActions.Awaiting(() => client.PutAsync<ProjectView>(ProjectEndpoints.RequiredChecksPathOf("nowhere"), settings.ToRequest(), None))
            .Should().ThrowAsync<DomainException>().WithMessage("Project 'nowhere' is not registered*");
        await FluentActions.Awaiting(() => client.PutAsync<ProjectView>(ProjectEndpoints.RequiredChecksPathOf("team/coanda"), new ProjectRequiredChecksRequest([" "]), None))
            .Should().ThrowAsync<DomainException>().WithMessage("*blank check name*");

        await daemon.StopAsync(None);
    }

    [Fact]
    public void The_command_needs_at_least_one_required_check()
    {
        new ProjectSetSettings { Name = "coanda" }.Validate().Successful.Should().BeFalse();
        new ProjectSetSettings { Name = "coanda", RequiredChecks = [Ubuntu] }.Validate().Successful.Should().BeTrue();
    }
}
