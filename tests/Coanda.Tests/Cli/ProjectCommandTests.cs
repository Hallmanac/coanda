using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Tests.Daemon;

namespace Coanda.Tests.Cli;

/// <summary>What <c>coa project add</c> sends for its <c>--build</c> and <c>--test</c> options, and what <c>coa project show</c> prints for them.</summary>
public sealed class ProjectCommandTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Without_build_or_test_the_project_shows_the_dotnet_defaults_the_gates_run()
    {
        IReadOnlyList<(string Key, string Value)> rows = await AddThenShowAsync(new ProjectAddSettings { Name = "plain", Repo = "git@example.invalid:plain.git", Branch = "main" });

        rows.Should().Contain(("Build", "dotnet build")).And.Contain(("Test", "dotnet test"));
    }

    [Fact]
    public async Task With_build_and_test_the_project_shows_the_commands_it_was_given()
    {
        IReadOnlyList<(string Key, string Value)> rows = await AddThenShowAsync(new ProjectAddSettings
        {
            Name = "custom", Repo = "git@example.invalid:custom.git", Branch = "main", Build = "make build [fast]", Test = "make test",
        });

        rows.Should().Contain(("Build", "make build [[fast]]"), "the command is printed as text, not markup").And.Contain(("Test", "make test"));
    }

    [Fact]
    public async Task With_only_one_of_them_the_other_stays_on_its_default()
    {
        IReadOnlyList<(string Key, string Value)> rows = await AddThenShowAsync(new ProjectAddSettings
        {
            Name = "half", Repo = "git@example.invalid:half.git", Branch = "main", Test = "npm test",
        });

        rows.Should().Contain(("Build", "dotnet build")).And.Contain(("Test", "npm test"));
    }

    [Theory]
    [InlineData(" ", null, "Project 'blank' was given a blank build command*")]
    [InlineData(null, "", "Project 'blank' was given a blank test command*")]
    public async Task A_blank_build_or_test_command_is_refused_as_a_blank_remote_is(string? build, string? test, string refusal)
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        var settings = new ProjectAddSettings { Name = "blank", Repo = "git@example.invalid:blank.git", Branch = "main", Build = build, Test = test };

        await FluentActions.Awaiting(() => client.PostAsync<ProjectView>("/projects", settings.ToRequest(), None))
            .Should().ThrowAsync<DomainException>().WithMessage(refusal);
        await FluentActions.Awaiting(() => client.GetAsync<ProjectView>("/projects/blank", None))
            .Should().ThrowAsync<DomainException>().WithMessage("Project 'blank' is not registered*", "nothing was recorded");

        await daemon.StopAsync(None);
    }

    private static async Task<IReadOnlyList<(string Key, string Value)>> AddThenShowAsync(ProjectAddSettings settings)
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        await client.PostAsync<ProjectView>("/projects", settings.ToRequest(), None);
        ProjectView shown = await client.GetAsync<ProjectView>(ProjectEndpoints.PathOf(settings.Name), None);

        await daemon.StopAsync(None);
        return ProjectShowCommand.ShowRows(shown);
    }
}
