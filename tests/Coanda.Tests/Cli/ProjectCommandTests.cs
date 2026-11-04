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

        Assert.Contains(("Build", "dotnet build"), rows);
        Assert.Contains(("Test", "dotnet test"), rows);
    }

    [Fact]
    public async Task With_build_and_test_the_project_shows_the_commands_it_was_given()
    {
        IReadOnlyList<(string Key, string Value)> rows = await AddThenShowAsync(new ProjectAddSettings
        {
            Name = "custom", Repo = "git@example.invalid:custom.git", Branch = "main", Build = "make build [fast]", Test = "make test",
        });

        // the command is printed as text, not markup
        Assert.Contains(("Build", "make build [[fast]]"), rows);
        Assert.Contains(("Test", "make test"), rows);
    }

    [Fact]
    public async Task With_only_one_of_them_the_other_stays_on_its_default()
    {
        IReadOnlyList<(string Key, string Value)> rows = await AddThenShowAsync(new ProjectAddSettings
        {
            Name = "half", Repo = "git@example.invalid:half.git", Branch = "main", Test = "npm test",
        });

        Assert.Contains(("Build", "dotnet build"), rows);
        Assert.Contains(("Test", "npm test"), rows);
    }

    [Theory]
    [InlineData(" ", null, "Project 'blank' was given a blank build command")]
    [InlineData(null, "", "Project 'blank' was given a blank test command")]
    public async Task A_blank_build_or_test_command_is_refused_as_a_blank_remote_is(string? build, string? test, string refusal)
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        var settings = new ProjectAddSettings { Name = "blank", Repo = "git@example.invalid:blank.git", Branch = "main", Build = build, Test = test };

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ProjectView>("/projects", settings.ToRequest(), None));
        Assert.StartsWith(refusal, refused.Message, StringComparison.Ordinal);
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.GetAsync<ProjectView>("/projects/blank", None));
        // nothing was recorded
        Assert.StartsWith("Project 'blank' is not registered", exception.Message, StringComparison.Ordinal);

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
