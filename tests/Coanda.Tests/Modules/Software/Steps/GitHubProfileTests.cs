using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Modules;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Modules.Software.Repositories;
using Coanda.Sdk.Commands;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Modules.Software.GitHub;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Modules.Software.Steps;

/// <summary>
/// The GitHub token the open-pr and gated-merge steps act with, read through the credential profile
/// selected for the software module in the project on this node: with none, the node's default account,
/// which <c>gh auth token</c> prints; with one, only the token the profile's <c>github-token</c> file
/// holds, never falling back to the default account. A test makes a profile by writing into the module's
/// area of the node home. No token appears in an event, a pause, or the log.
/// </summary>
public sealed class GitHubProfileTests : IAsyncLifetime
{
    private const string Work = "work";
    private const string Personal = "personal";
    private const string WorkToken = "ghp_workTokenNoRecordHolds00000000000000";
    private const string PersonalToken = "ghp_personalTokenNoRecordHolds000000000";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ScriptedCommandRunner runner = new();
    private readonly FakeGitHub github = new();
    private readonly RecordingLogger log = new();
    private SoftwareNode node = null!;

    public async Task InitializeAsync() => node = await SoftwareNode.StartAsync(runner, github, log: log, projects: ["alpha", "beta"]);

    public async Task DisposeAsync() => await node.DisposeAsync();

    [Fact]
    public async Task With_no_profile_selected_the_token_is_the_github_clis_default_account()
    {
        await OpenAsync("alpha");

        Assert.Single(runner.Commands, c => c.Program == "gh" && string.Join(' ', c.Arguments) == "auth token --hostname github.com");
        Assert.All(github.Requests, r => Assert.Equal($"Bearer {ScriptedCommandRunner.GitHubToken}", r.Authorization));
    }

    [Fact]
    public async Task Two_projects_on_one_node_read_the_tokens_of_two_different_profiles()
    {
        WriteProfile(Work, WorkToken);
        WriteProfile(Personal, PersonalToken);
        await SelectAsync("alpha", Work);
        await SelectAsync("beta", Personal);

        await OpenAsync("alpha");
        int alphaRequests = github.Requests.Count;
        await OpenAsync("beta");

        Assert.All(github.Requests.Take(alphaRequests), r => Assert.Equal($"Bearer {WorkToken}", r.Authorization));
        Assert.All(github.Requests.Skip(alphaRequests), r => Assert.Equal($"Bearer {PersonalToken}", r.Authorization));
        Assert.NotEmpty(github.Requests.Skip(alphaRequests));
        // Neither project asked the GitHub CLI for the default account's token.
        Assert.DoesNotContain(runner.Commands, c => c.Program == "gh");
        await NoTokenIsRecordedAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two tokens")]
    public async Task A_selected_profile_that_yields_no_token_pauses_naming_the_setting_and_falls_back_to_nothing(string? token)
    {
        WriteProfile(Work, token);
        await SelectAsync("alpha", Work);
        string tokenPath = Path.Combine(node.Home.ModuleAreaOf(SoftwareSteps.Module), Work, SoftwareSettings.GitHubTokenFile);

        foreach ((string step, string done) in new[] { ("open-pr", "pushed nothing"), ("gated-merge", "merged nothing") })
        {
            Parcel parcel = await node.TaskAsync($"No token for {step}", "alpha");
            await node.Parcels.SetFieldAsync(Principal.LocalOwner, "alpha", parcel.Id, TaskFields.PullRequest, FieldValue.OfReference("17", FakeGitHub.Url), None);
            (Guid flowRunId, Guid dispatchId) = await node.HeldAsync(parcel.Id, step, "alpha");

            SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(
                await node.DispatchAsync(SoftwareSteps.Canonical(step), parcel.Id, flowRunId, dispatchId, "alpha"));
            await node.StopAsync(flowRunId, "alpha");

            Assert.StartsWith($"Step '{step}' {done}: no GitHub token is available, because the credential profile 'work' yields no GitHub token: {tokenPath} ", paused.Reason, StringComparison.Ordinal);
            Assert.Equal(
                $"Put a GitHub token in {tokenPath}, or select one of this node's credential profiles that holds a GitHub token for the software module's setting 'credential-profile' in project 'alpha' (coa project module select-profile alpha software <profile>), then resume the flow run to perform step '{step}' again.",
                paused.Lever);
            Assert.DoesNotContain("two tokens", paused.Reason, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(runner.Commands, c => c.Program == "gh");
        Assert.Empty(github.Requests);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("push", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_selected_profile_the_node_no_longer_holds_pauses_rather_than_reading_the_default_account()
    {
        WriteProfile(Work, WorkToken);
        await SelectAsync("alpha", Work);
        Directory.Delete(Path.Combine(node.Home.ModuleAreaOf(SoftwareSteps.Module), Work), recursive: true);
        Parcel parcel = await node.TaskAsync("Gone", "alpha");
        (Guid flowRunId, Guid dispatchId) = await node.HeldAsync(parcel.Id, "open-pr", "alpha");

        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(
            await node.DispatchAsync(SoftwareSteps.OpenPr, parcel.Id, flowRunId, dispatchId, "alpha"));

        Assert.Contains("the credential profile 'work' selected for the software module is one this node no longer holds", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("coa project module select-profile alpha software <profile>", paused.Lever, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, c => c.Program == "gh");
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task The_push_uses_the_nodes_own_git_credentials_whatever_profile_is_selected()
    {
        WriteProfile(Work, WorkToken);
        await SelectAsync("alpha", Work);

        await OpenAsync("alpha");

        CommandLine push = Assert.Single(runner.Commands, c => c.Program == "git" && c.GitArguments().FirstOrDefault() == "push");
        Assert.DoesNotContain(WorkToken, string.Join(' ', push.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain(push.Environment ?? new Dictionary<string, string>(), e => e.Value.Contains(WorkToken, StringComparison.Ordinal));
    }

    /// <summary>Opens a pull request on a new task in <paramref name="project"/>, as the open-pr step does, and checks it opened.</summary>
    private async Task OpenAsync(string project)
    {
        Parcel parcel = await node.TaskAsync($"Open in {project}", project);
        await node.Parcels.BriefAsync(Principal.LocalOwner, project, parcel.Id, pullRequest: false, cancellationToken: None);
        runner.AddBranch(node.Home.Clones().ProjectClonePath(project), ProjectClones.BranchOf(parcel.Id), ScriptedCommandRunner.PrimaryHead);
        (Guid flowRunId, Guid dispatchId) = await node.HeldAsync(parcel.Id, "open-pr", project);

        SystemStepAnswer answer = await node.DispatchAsync(SoftwareSteps.OpenPr, parcel.Id, flowRunId, dispatchId, project);
        await node.StopAsync(flowRunId, project);

        Assert.Equal("opened", Assert.IsType<SystemStepAnswer.Done>(answer).Result.GetProperty("verdict").GetString());
    }

    /// <summary>Makes the profile <paramref name="profile"/> in the module's area, holding <paramref name="token"/> in its token file, or no file when it is null.</summary>
    private void WriteProfile(string profile, string? token)
    {
        string directory = Path.Combine(node.Home.ModuleAreaOf(SoftwareSteps.Module), profile);
        Directory.CreateDirectory(directory);
        if (token is not null)
        {
            File.WriteAllText(Path.Combine(directory, SoftwareSettings.GitHubTokenFile), $"{token}\n");
        }
    }

    private Task<ModuleSettingsShown> SelectAsync(string project, string profile) =>
        node.Services.GetRequiredService<ModuleSettingsService>().SelectProfileAsync(Principal.LocalOwner, project, SoftwareSteps.Module, profile, None);

    /// <summary>Neither token is in any event the store holds, for any project or the node, or in the log.</summary>
    private async Task NoTokenIsRecordedAsync()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = node.Home.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM fi_events";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(None);
        int events = 0;
        while (await reader.ReadAsync(None))
        {
            string data = reader.GetString(0);
            Assert.DoesNotContain(WorkToken, data, StringComparison.Ordinal);
            Assert.DoesNotContain(PersonalToken, data, StringComparison.Ordinal);
            events++;
        }

        Assert.True(events > 0, "the store holds events to look through");
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(WorkToken, StringComparison.Ordinal) || e.Message.Contains(PersonalToken, StringComparison.Ordinal));
    }
}
