using System.Net;
using System.Net.Http.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>An operator's pause, resume, and stop of a flow run, each through its own daemon endpoint.</summary>
public sealed class FlowRunControlApiTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_pause_while_an_agent_step_runs_drops_its_answer_and_a_resume_performs_the_step_again_to_its_answer()
    {
        using var temp = new TemporaryHome();
        // The pause stops the held session before it collects, so the resume's spawn takes the script's first entry.
        var executor = new GatedExecutor(new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 2)],
        }));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        using HttpClient http = Http(daemon);
        Flow single = await Publishing.FlowAsync(daemon, Project, new Flow
        {
            Name = "one-agent-step",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 2,
            Steps =
            [
                new FlowStep
                {
                    Name = "build",
                    Agent = "build",
                    AnswerSchema = "build",
                    Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
                },
            ],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Paused mid-agent-step", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, single.Name);

        // Collecting, not Spawned: the pause is meant to find the agent step held in its collect, past
        // the spawn and the session record that follows it.
        await executor.Collecting.WaitAsync(TimeSpan.FromSeconds(15));

        await RefusedAsync(http, daemon, $"/flow-runs/{started.Id}/pause", new FlowRunPauseRequest(Project, " "), "A pause records why the flow run stopped; give a reason.");
        await RefusedAsync(http, daemon, $"/flow-runs/{started.Id}/stop", new FlowRunStopRequest(Project, ""), "Stopping a flow run records why; give a reason.");
        // a refused pause or stop changes nothing
        Assert.Equal("running", (await FlowRunDriver.ShowAsync(client, Project, started.Id)).Status);

        FlowRunView paused = await FlowRunDriver.PauseAsync(client, Project, started.Id, "Waiting on the API owner.");
        Assert.Equal("paused", paused.Status);

        // The pause cancels the held collect on the pool, so the gate opens only once the held
        // session is stopped; opened sooner, the held collect could pass it and take the entry.
        await FlowRunDriver.UntilAsync(() => executor.Inner.Stops.Count == 1, "the held session is stopped");
        executor.Open();

        FlowRunView held = await FlowRunDriver.ShowAsync(client, Project, started.Id);
        Assert.Equal("paused", held.Status);
        Assert.Equal("Waiting on the API owner.", held.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), held.Lever);
        // the held agent step's late answer is dropped
        Assert.Null(Assert.Single(held.Steps).Answer);
        Assert.Single(executor.Inner.Spawns);

        FlowRunView resumed = await FlowRunDriver.ResumeAsync(client, Project, started.Id, "The API owner answered.");
        Assert.Equal("running", resumed.Status);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        // the resume performs the step again
        Assert.Equal(2, executor.Inner.Spawns.Count);
        FlowRunStepView build = Assert.Single(ended.Steps);
        Assert.Equal("answer 2", build.Answer.GetValueOrDefault().GetProperty("summary").GetString());

        await RefusedAsync(http, daemon, $"/flow-runs/{started.Id}/stop", new FlowRunStopRequest(Project, "Too late."), $"Flow run {Ids.Short(started.Id)} has ended; only a live flow run stops.");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_stop_of_a_live_flow_run_releases_its_parcel_and_each_verb_refuses_a_flow_run_in_the_wrong_state_with_422()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        using HttpClient http = Http(daemon);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Stopped by hand", null), None);

        // The stock executor holds no script, so the flow run pauses at its first agent step.
        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, "build");
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        string id = Ids.Short(paused.Id);

        await RefusedAsync(http, daemon, $"/flow-runs/{paused.Id}/pause", new FlowRunPauseRequest(Project, "Again."), $"Flow run {id} is paused; only a running flow run can pause it.");

        FlowRunView stopped = await FlowRunDriver.StopAsync(client, Project, paused.Id, "Taking this by hand.");
        Assert.Equal("stopped", stopped.Status);
        Assert.Equal("Taking this by hand.", stopped.StopReason);
        // a stopped flow run releases its parcel
        Assert.Null((await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).LiveFlowRunId);

        await RefusedAsync(http, daemon, $"/flow-runs/{paused.Id}/resume", new FlowRunResumeRequest(Project, null), $"Flow run {id} has been stopped; only a paused flow run resumes.");
        await RefusedAsync(http, daemon, $"/flow-runs/{paused.Id}/stop", new FlowRunStopRequest(Project, "Twice."), $"Flow run {id} has been stopped; only a live flow run stops.");
        await RefusedAsync(http, daemon, $"/flow-runs/{paused.Id}/pause", new FlowRunPauseRequest(Project, "Late."), $"Flow run {id} has been stopped; only a running flow run can pause it.");

        FlowRunView after = await FlowRunDriver.ShowAsync(client, Project, paused.Id);
        Assert.Equal("stopped", after.Status);
        // every refusal leaves the flow run as it was
        Assert.Equal(stopped.UpdatedAt, after.UpdatedAt);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Each_verb_refuses_a_flow_run_id_the_project_does_not_hold_with_422_and_the_services_rule()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using HttpClient http = Http(daemon);
        Guid unknown = Ids.New();
        string rule = $"No flow run {Ids.Short(unknown)} in project '{Project}'.";

        await RefusedAsync(http, daemon, $"/flow-runs/{unknown}/pause", new FlowRunPauseRequest(Project, "Hold on."), rule);
        await RefusedAsync(http, daemon, $"/flow-runs/{unknown}/resume", new FlowRunResumeRequest(Project, null), rule);
        await RefusedAsync(http, daemon, $"/flow-runs/{unknown}/stop", new FlowRunStopRequest(Project, "Not wanted."), rule);

        await daemon.StopAsync(None);
    }

    private static HttpClient Http(RunningDaemon daemon) => new() { BaseAddress = new Uri(daemon.Info.BaseAddress) };

    private static async Task RefusedAsync(HttpClient http, RunningDaemon daemon, string path, object body, string rule)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, body.GetType()) };
        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);

        using HttpResponseMessage response = await http.SendAsync(request, None);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        ErrorView? error = await response.Content.ReadFromJsonAsync<ErrorView>(None);
        Assert.NotNull(error);
        Assert.Equal(rule, error.Error);
    }
}
