using System.Net;
using System.Net.Http.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Flows;

namespace Coanda.Tests.Daemon.Flows;

/// <summary>An operator's pause, resume, and stop of a flow, each through its own daemon endpoint.</summary>
public sealed class FlowControlApiTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_pause_while_a_judgment_runs_drops_its_answer_but_keeps_its_cost_and_a_resume_performs_the_step_again_to_its_answer()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1), Scripts.Answer("done", 2)],
        }));
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
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

        FlowView started = await Flowing.StartAsync(client, Project, parcel.Id, single.Name);

        // Collecting, not Spawned: the session is recorded on the flow after the spawn returns, and a
        // pause that lands before it is refused as a concurrent change.
        await executor.Collecting.WaitAsync(TimeSpan.FromSeconds(15));

        await RefusedAsync(http, daemon, $"/flows/{started.Id}/pause", new FlowPauseRequest(Project, " "), "A pause records why the flow stopped; give a reason.");
        await RefusedAsync(http, daemon, $"/flows/{started.Id}/stop", new FlowStopRequest(Project, ""), "Stopping a flow records why; give a reason.");
        (await Flowing.ShowAsync(client, Project, started.Id)).Status.Should().Be("running", "a refused pause or stop changes nothing");

        FlowView paused = await Flowing.PauseAsync(client, Project, started.Id, "Waiting on the API owner.");
        paused.Status.Should().Be("paused");

        executor.Open();
        await Flowing.UntilAsync(() => executor.Inner.Stops.Count == 1, "the held session is stopped");

        // The usage is recorded before the session is stopped, so the stop means it is in.
        FlowView held = await Flowing.ShowAsync(client, Project, started.Id);
        held.Status.Should().Be("paused");
        held.PauseReason.Should().Be("Waiting on the API owner.");
        held.Lever.Should().Be(FlowLevers.PerformAgain("build"));
        held.Steps.Should().ContainSingle().Which.Answer.Should().BeNull("the held judgment's late answer is dropped");
        executor.Inner.Spawns.Should().ContainSingle();
        held.Cost.Should().Be(Scripts.Usage(1).CostUsd, "the dropped answer's agent still spent what it reported");

        FlowView resumed = await Flowing.ResumeAsync(client, Project, started.Id, "The API owner answered.");
        resumed.Status.Should().Be("running");
        FlowView ended = await Flowing.UntilAsync(client, Project, started.Id, "ended");

        executor.Inner.Spawns.Should().HaveCount(2, "the resume performs the step again");
        FlowStepView build = ended.Steps.Should().ContainSingle().Subject;
        build.Answer.GetValueOrDefault().GetProperty("summary").GetString().Should().Be("answer 2");
        ended.Cost.Should().Be(Scripts.Usage(1).CostUsd + Scripts.Usage(2).CostUsd, "both spawns' spend is recorded");

        await RefusedAsync(http, daemon, $"/flows/{started.Id}/stop", new FlowStopRequest(Project, "Too late."), $"Flow {Ids.Short(started.Id)} has ended; only a live flow stops.");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_stop_of_a_live_flow_releases_its_parcel_and_each_verb_refuses_a_flow_in_the_wrong_state_with_422()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        using HttpClient http = Http(daemon);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Stopped by hand", null), None);

        // The stock executor holds no script, so the flow pauses at its first judgment.
        FlowView started = await Flowing.StartAsync(client, Project, parcel.Id, "build");
        FlowView paused = await Flowing.UntilAsync(client, Project, started.Id, "paused");
        string id = Ids.Short(paused.Id);

        await RefusedAsync(http, daemon, $"/flows/{paused.Id}/pause", new FlowPauseRequest(Project, "Again."), $"Flow {id} is paused; only a running flow can pause it.");

        FlowView stopped = await Flowing.StopAsync(client, Project, paused.Id, "Taking this by hand.");
        stopped.Status.Should().Be("stopped");
        stopped.StopReason.Should().Be("Taking this by hand.");
        (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).LiveFlowId.Should().BeNull("a stopped flow releases its parcel");

        await RefusedAsync(http, daemon, $"/flows/{paused.Id}/resume", new FlowResumeRequest(Project, null), $"Flow {id} has been stopped; only a paused flow resumes.");
        await RefusedAsync(http, daemon, $"/flows/{paused.Id}/stop", new FlowStopRequest(Project, "Twice."), $"Flow {id} has been stopped; only a live flow stops.");
        await RefusedAsync(http, daemon, $"/flows/{paused.Id}/pause", new FlowPauseRequest(Project, "Late."), $"Flow {id} has been stopped; only a running flow can pause it.");

        FlowView after = await Flowing.ShowAsync(client, Project, paused.Id);
        after.Status.Should().Be("stopped");
        after.UpdatedAt.Should().Be(stopped.UpdatedAt, "every refusal leaves the flow as it was");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Each_verb_refuses_a_flow_id_the_project_does_not_hold_with_422_and_the_services_rule()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        using HttpClient http = Http(daemon);
        Guid unknown = Ids.New();
        string rule = $"No flow {Ids.Short(unknown)} in project '{Project}'.";

        await RefusedAsync(http, daemon, $"/flows/{unknown}/pause", new FlowPauseRequest(Project, "Hold on."), rule);
        await RefusedAsync(http, daemon, $"/flows/{unknown}/resume", new FlowResumeRequest(Project, null), rule);
        await RefusedAsync(http, daemon, $"/flows/{unknown}/stop", new FlowStopRequest(Project, "Not wanted."), rule);

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
