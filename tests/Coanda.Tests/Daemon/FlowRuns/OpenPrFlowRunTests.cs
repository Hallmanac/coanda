using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.GitHub;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>The shipped build flow's open-pr step as a running daemon performs it, on a scripted runner and a fake GitHub.</summary>
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
        Assert.Equal(OpenPrSystemStep.OpenedVerdict, opened.GetProperty("verdict").GetString());
        Assert.Equal(FakeGitHub.Number, opened.GetProperty("number").GetInt32());
        Assert.Equal(FakeGitHub.Url, opened.GetProperty("url").GetString());
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, opened.GetProperty("head").GetString());
        Assert.NotEmpty(github.Requests);
        Assert.All(github.Requests, r => Assert.True(r.Authorization == $"Bearer {ScriptedCommandRunner.GitHubToken}"));
        Assert.DoesNotContain(ScriptedCommandRunner.GitHubToken, JsonSerializer.Serialize(flowRun), StringComparison.Ordinal);
        Assert.DoesNotContain(ScriptedCommandRunner.GitHubToken, JsonSerializer.Serialize(await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)), StringComparison.Ordinal);

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

        Assert.Equal("open-pr", paused.CurrentStep);
        Assert.Contains("status 422", paused.PauseReason, StringComparison.Ordinal);
        Assert.Contains("Validation Failed", paused.PauseReason, StringComparison.Ordinal);
        Assert.DoesNotContain(ScriptedCommandRunner.GitHubToken, paused.PauseReason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("open-pr"), paused.Lever);
        Assert.DoesNotContain(ScriptedCommandRunner.GitHubToken, JsonSerializer.Serialize(paused), StringComparison.Ordinal);

        github.On(r => r.IsOpen && r.Body is not null, r => FakeGitHub.Json(HttpStatusCode.Created, new JsonObject
        {
            ["number"] = FakeGitHub.Number,
            ["html_url"] = FakeGitHub.Url,
            ["head"] = new JsonObject { ["ref"] = $"coanda/{Ids.Short(parcel.Id)}" },
        }));
        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "GitHub is back.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal("pr-opened", ended.Outcome);
        await FlowRunDriver.MergedAsync(client, Project, parcel.Id, flowRunId);
        // the build moved the task to review, and the merge that followed closed it
        Assert.Equal("closed", (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).Stage);
        Assert.Empty(github.Unexpected);

        await daemon.StopAsync(None);
    }
}
