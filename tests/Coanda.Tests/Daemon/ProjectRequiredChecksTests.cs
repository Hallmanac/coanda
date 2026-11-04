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
        Assert.Empty((await projects.AddAsync(Owner, "coanda", Remote, "main", None)).RequiredChecks);

        Project set = await projects.SetRequiredChecksAsync(Owner, "coanda", [Ubuntu, Windows], None);
        Assert.Equal([Ubuntu, Windows], set.RequiredChecks);
        Assert.Equal([Ubuntu, Windows], (await projects.ShowAsync(Owner, "coanda", None))?.RequiredChecks);

        await projects.SetRequiredChecksAsync(Owner, "coanda", [" lint "], None);
        // names are stored exactly as given and the list is replaced
        Assert.Equal([" lint "], (await projects.ShowAsync(Owner, "coanda", None))?.RequiredChecks);
        // setting checks changes nothing else on the record
        Assert.Equal(Remote, (await projects.ShowAsync(Owner, "coanda", None))?.Remote);
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

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => projects.SetRequiredChecksAsync(Owner, "coanda", [Windows, blank], None));
        Assert.StartsWith("Project 'coanda' was given a blank check name", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => projects.SetRequiredChecksAsync(Owner, "coanda", [], None));
        Assert.StartsWith("Project 'coanda' needs at least one check", exception2.Message, StringComparison.Ordinal);

        Assert.Equal([Ubuntu], (await projects.ShowAsync(Owner, "coanda", None))?.RequiredChecks);
    }

    [Fact]
    public async Task A_project_with_no_record_is_refused()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => new ProjectService(store, TimeProvider.System).SetRequiredChecksAsync(Owner, "nowhere", [Ubuntu], None));
        Assert.Equal(ProjectDecider.Unregistered("nowhere"), exception.Message);
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

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => projects.SetRequiredChecksAsync(new Principal("someone", role), "coanda", [Ubuntu], None));
        Assert.Equal($"'project.set' is not open to the {role} role.", exception.Message);

        Project? shown = await projects.ShowAsync(Owner, "coanda", None);
        Assert.NotNull(shown);
        Assert.Empty(shown.RequiredChecks);
        Assert.Equivalent(new[] { Role.Owner }, CommandRegistry.Entries.Single(e => e.Name == "project.set").AllowedRoles, strict: true);
    }

    [Fact]
    public async Task The_endpoint_sets_the_checks_and_coa_project_show_prints_them()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await client.PostAsync<ProjectView>("/projects", new ProjectAddRequest("team/coanda", Remote, "main"), None);
        ProjectView added = await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf("team/coanda"), None);
        Assert.Contains(ProjectShowCommand.ShowRows(added), row => row.Key == "Required checks" && row.Value.StartsWith("none set", StringComparison.Ordinal));

        var settings = new ProjectSetSettings { Name = "team/coanda", RequiredChecks = [Ubuntu, "lint [fast]"] };
        ProjectView set = await client.PutAsync<ProjectView>(ProjectEndpoints.RequiredChecksPathOf(settings.Name), settings.ToRequest(), None);

        Assert.Equal("/projects/team/coanda/required-checks", ProjectEndpoints.RequiredChecksPathOf("team/coanda"));
        Assert.Equal([Ubuntu, "lint [fast]"], set.RequiredChecks);
        ProjectView shown = await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf("team/coanda"), None);
        // the checks are printed as text, not markup
        Assert.Contains(("Required checks", $"{Ubuntu}, lint [[fast]]"), ProjectShowCommand.ShowRows(shown));

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PutAsync<ProjectView>(ProjectEndpoints.RequiredChecksPathOf("nowhere"), settings.ToRequest(), None));
        Assert.StartsWith("Project 'nowhere' is not registered", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => client.PutAsync<ProjectView>(ProjectEndpoints.RequiredChecksPathOf("team/coanda"), new ProjectRequiredChecksRequest([" "]), None));
        Assert.Contains("blank check name", exception2.Message, StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }

    [Fact]
    public void The_command_needs_at_least_one_required_check()
    {
        Assert.False(new ProjectSetSettings { Name = "coanda" }.Validate().Successful);
        Assert.True(new ProjectSetSettings { Name = "coanda", RequiredChecks = [Ubuntu] }.Validate().Successful);
    }
}
