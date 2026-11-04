using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.GitHub;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>The shipped build contour's open-pr step as a running daemon performs it, on a scripted runner and a fake GitHub.</summary>
public sealed class OpenPrFlowRunTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_clean_build_flow_run_records_the_opened_result_and_nothing_it_records_holds_the_token()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        var github = new FakeGitHub();
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = runner, GitHubHandler = github.Handler }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Open it");

        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        JsonElement opened = flowRun.Steps.Single(s => s.Step == "open-pr").Result.GetValueOrDefault();
        opened.GetProperty("verdict").GetString().Should().Be(OpenPrAction.OpenedVerdict);
        opened.GetProperty("number").GetInt32().Should().Be(FakeGitHub.Number);
        opened.GetProperty("url").GetString().Should().Be(FakeGitHub.Url);
        opened.GetProperty("head").GetString().Should().Be(ScriptedCommandRunner.PrimaryHead);
        github.Requests.Should().NotBeEmpty().And.OnlyContain(r => r.Authorization == $"Bearer {ScriptedCommandRunner.GitHubToken}");
        JsonSerializer.Serialize(flowRun).Should().NotContain(ScriptedCommandRunner.GitHubToken);
        JsonSerializer.Serialize(await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).Should().NotContain(ScriptedCommandRunner.GitHubToken);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_refusal_from_GitHub_pauses_the_flow_run_at_open_pr_without_the_token_and_a_resume_opens_the_one_pull_request_that_the_merge_follows()
    {
        using var temp = new TemporaryHome();
        var github = new FakeGitHub();
        github.Refuse(r => r.IsOpen, HttpStatusCode.UnprocessableEntity, "Validation Failed");
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), GitHubHandler = github.Handler }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Refused once");
        Guid flowRunId = (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id;

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        paused.CurrentStep.Should().Be("open-pr");
        paused.PauseReason.Should().Contain("status 422").And.Contain("Validation Failed").And.NotContain(ScriptedCommandRunner.GitHubToken);
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("open-pr"));
        JsonSerializer.Serialize(paused).Should().NotContain(ScriptedCommandRunner.GitHubToken);

        github.On(r => r.IsOpen && r.Body is not null, r => FakeGitHub.Json(HttpStatusCode.Created, new JsonObject
        {
            ["number"] = FakeGitHub.Number,
            ["html_url"] = FakeGitHub.Url,
            ["head"] = new JsonObject { ["ref"] = $"coanda/{Ids.Short(parcel.Id)}" },
        }));
        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "GitHub is back.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        ended.Outcome.Should().Be("pr-opened");
        await FlowRunDriver.MergedAsync(client, Project, parcel.Id, flowRunId);
        (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).State
            .Should().Be("closed", "the build moved the task to review, and the merge that followed closed it");
        github.Unexpected.Should().BeEmpty();

        await daemon.StopAsync(None);
    }
}
