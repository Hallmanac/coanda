using System.Net;
using System.Net.Http.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Contours;
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
    public async Task A_pause_while_a_judgment_runs_drops_its_answer_and_a_resume_performs_the_step_again_to_its_answer()
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
        Contour single = await Publishing.ContourAsync(daemon, Project, new Contour
        {
            Name = "one-judgment",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 2,
            Steps =
            [
                new ContourStep
                {
                    Name = "build",
                    Agent = "build",
                    AnswerSchema = "build",
                    Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of("done"), VisitCap = 1, Target = Contour.End }],
                },
            ],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Paused mid-judgment", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, single.Name);

        // Collecting, not Spawned: the pause is meant to find the judgment held in its collect, past
        // the spawn and the session record that follows it.
        await executor.Collecting.WaitAsync(TimeSpan.FromSeconds(15));

        await RefusedAsync(http, daemon, $"/flow-runs/{started.Id}/pause", new FlowRunPauseRequest(Project, " "), "A pause records why the flow run stopped; give a reason.");
        await RefusedAsync(http, daemon, $"/flow-runs/{started.Id}/stop", new FlowRunStopRequest(Project, ""), "Stopping a flow run records why; give a reason.");
        (await FlowRunDriver.ShowAsync(client, Project, started.Id)).Status.Should().Be("running", "a refused pause or stop changes nothing");

        FlowRunView paused = await FlowRunDriver.PauseAsync(client, Project, started.Id, "Waiting on the API owner.");
        paused.Status.Should().Be("paused");

        // The pause cancels the held collect on the pool, so the gate opens only once the held
        // session is stopped; opened sooner, the held collect could pass it and take the entry.
        await FlowRunDriver.UntilAsync(() => executor.Inner.Stops.Count == 1, "the held session is stopped");
        executor.Open();

        FlowRunView held = await FlowRunDriver.ShowAsync(client, Project, started.Id);
        held.Status.Should().Be("paused");
        held.PauseReason.Should().Be("Waiting on the API owner.");
        held.Lever.Should().Be(FlowRunLevers.PerformAgain("build"));
        held.Steps.Should().ContainSingle().Which.Answer.Should().BeNull("the held judgment's late answer is dropped");
        executor.Inner.Spawns.Should().ContainSingle();

        FlowRunView resumed = await FlowRunDriver.ResumeAsync(client, Project, started.Id, "The API owner answered.");
        resumed.Status.Should().Be("running");
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        executor.Inner.Spawns.Should().HaveCount(2, "the resume performs the step again");
        FlowRunStepView build = ended.Steps.Should().ContainSingle().Subject;
        build.Answer.GetValueOrDefault().GetProperty("summary").GetString().Should().Be("answer 2");

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

        // The stock executor holds no script, so the flow run pauses at its first judgment.
        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, "build");
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        string id = Ids.Short(paused.Id);

        await RefusedAsync(http, daemon, $"/flow-runs/{paused.Id}/pause", new FlowRunPauseRequest(Project, "Again."), $"Flow run {id} is paused; only a running flow run can pause it.");

        FlowRunView stopped = await FlowRunDriver.StopAsync(client, Project, paused.Id, "Taking this by hand.");
        stopped.Status.Should().Be("stopped");
        stopped.StopReason.Should().Be("Taking this by hand.");
        (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).LiveFlowRunId.Should().BeNull("a stopped flow run releases its parcel");

        await RefusedAsync(http, daemon, $"/flow-runs/{paused.Id}/resume", new FlowRunResumeRequest(Project, null), $"Flow run {id} has been stopped; only a paused flow run resumes.");
        await RefusedAsync(http, daemon, $"/flow-runs/{paused.Id}/stop", new FlowRunStopRequest(Project, "Twice."), $"Flow run {id} has been stopped; only a live flow run stops.");
        await RefusedAsync(http, daemon, $"/flow-runs/{paused.Id}/pause", new FlowRunPauseRequest(Project, "Late."), $"Flow run {id} has been stopped; only a running flow run can pause it.");

        FlowRunView after = await FlowRunDriver.ShowAsync(client, Project, paused.Id);
        after.Status.Should().Be("stopped");
        after.UpdatedAt.Should().Be(stopped.UpdatedAt, "every refusal leaves the flow run as it was");

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
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorView? error = await response.Content.ReadFromJsonAsync<ErrorView>(None);
        error.Should().NotBeNull();
        error.Error.Should().Be(rule);
    }
}
