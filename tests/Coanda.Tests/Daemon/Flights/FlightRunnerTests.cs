using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flights;
using Coanda.Core.Flows;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Flights;
using Coanda.Daemon.Store;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Flights;

public sealed class FlightRunnerTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly string[] CleanPath =
    [
        "worktree", "build", "gates", "adversarial-review", "conformance-review", "open-pr", "gated-merge", "move-to-review",
    ];

    [Fact]
    public async Task A_flight_started_through_the_endpoint_runs_the_build_flows_clean_path_to_its_end()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Scripts.CleanBuildExecutor();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flying.TaskAtBuildAsync(client, Project, "Fly the clean path");

        FlightView started = await Flying.StartAsync(client, Project, parcel.Id, "build");
        FlightView flight = await Flying.UntilAsync(client, Project, started.Id, "ended");

        flight.Outcome.Should().Be("pr-opened");
        flight.Steps.Select(s => s.Step).Should().Equal(CleanPath, "each go-to records the step it enters");
        flight.Steps.Should().OnlyContain(s => s.RecordedAt != null);
        ExpectJudgment(flight.Steps[1], ShippedAgents.Build, 1);
        ExpectJudgment(flight.Steps[3], ShippedAgents.ReviewerAdversarial, 2);
        ExpectJudgment(flight.Steps[4], ShippedAgents.ReviewerConformance, 3);
        flight.Steps[2].Result.GetValueOrDefault().GetProperty("placeholder").GetBoolean().Should().BeTrue();

        ParcelView after = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None);
        after.State.Should().Be("review");
        after.LiveFlightId.Should().BeNull();

        ScriptedSpawn build = executor.Spawns[0];
        build.WorkingDirectory.Should().Be(temp.Home.FlightWorkPath(Project, started.Id));
        build.Caps.Should().Be(new SpawnCaps(ShippedFlows.Build.Budget, DaemonOptions.DefaultJudgmentLimit));
        build.CredentialProfile.Should().BeNull();
        build.AnswerSchema.Should().Be("build");
        build.Context.Should().Contain("Title: Fly the clean path").And.Contain("Step: build");
        executor.Stops.Should().HaveCount(3, "the runner ends each judgment's session once its answer is in hand");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_parcel_move_the_move_parcel_step_records_carries_the_flights_principal()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flying.TaskAtBuildAsync(client, Project, "Who moved it");

        FlightView started = await Flying.StartAsync(client, Project, parcel.Id, "build");
        await Flying.UntilAsync(client, Project, started.Id, "ended");

        await using IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Project);
        var events = await session.Events.FetchStreamAsync(parcel.Id, token: None);
        ParcelMoved toReview = events.Select(e => e.Data).OfType<ParcelMoved>().Should().ContainSingle(m => m.ToState == "review").Subject;
        toReview.MovedBy.Should().Be(FlightRunner.PrincipalFor(started.Id).Id).And.Be($"flight:{started.Id}");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_review_that_stays_not_clean_past_its_cap_parks_with_the_interpreters_reason()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1)],
            ["reviewer-adversarial"] = [.. Enumerable.Range(1, 4).Select(n => Scripts.Answer("not-clean", n))],
            ["fix"] = [.. Enumerable.Range(1, 3).Select(n => Scripts.Answer("done", n))],
        });
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flying.TaskAtBuildAsync(client, Project, "Never clean");

        FlightView started = await Flying.StartAsync(client, Project, parcel.Id, "build");
        FlightView flight = await Flying.UntilAsync(client, Project, started.Id, "parked");

        flight.CurrentStep.Should().Be("adversarial-review");
        flight.ParkReason.Should().Be("Step 'adversarial-review' reached the visit cap of 3 on its transition to 'fix'.");
        flight.Lever.Should().Be(FlightLevers.PerformAgain("adversarial-review"));
        flight.Steps.Count(s => s.Step == "adversarial-review").Should().Be(4);
        flight.Steps[^1].Answer.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("not-clean");

        ParcelView after = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None);
        after.State.Should().Be("build", "a flight park leaves the parcel in its state");
        after.LiveFlightId.Should().Be(started.Id);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_wait_for_human_step_parks_with_its_lever_and_a_resume_through_the_flight_service_carries_the_flight_to_its_end()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var client = new DaemonClient(temp.Home);
        var hold = new FlowDefinition
        {
            Name = "hold-for-a-person",
            Accepts = ["task"],
            Outcome = "approved",
            Budget = 1m,
            SpawnCap = 1,
            Steps =
            [
                new FlowStep
                {
                    Name = "approve",
                    Action = CoreActions.WaitForHuman,
                    Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of("Read the task and resume to approve it.") },
                    Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = FlowDefinition.End }],
                },
            ],
        };
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, hold.Name, FlowDefinition.CanonicalJson(hold), hold.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Needs a nod", null), None);

        FlightView started = await Flying.StartAsync(client, Project, parcel.Id, hold.Name);
        FlightView parked = await Flying.UntilAsync(client, Project, started.Id, "parked");
        parked.Lever.Should().Be("Read the task and resume to approve it.");
        parked.ParkReason.Should().Contain("Step 'approve'");

        await daemon.Services.GetRequiredService<FlightService>().ResumeAsync(Principal.LocalOwner, Project, started.Id, "Approved.", None);
        FlightView ended = await Flying.UntilAsync(client, Project, started.Id, "ended");

        ended.Outcome.Should().Be("approved");
        FlightStepView approve = ended.Steps.Should().ContainSingle().Subject;
        approve.Result.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("done");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_second_request_while_a_judgment_runs_does_not_perform_the_step_twice()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(Scripts.CleanBuildExecutor());
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        using var client = new DaemonClient(temp.Home);
        IFlightAdvancer advancer = daemon.Services.GetRequiredService<IFlightAdvancer>();
        ParcelView parcel = await Flying.TaskAtBuildAsync(client, Project, "Only once");

        FlightView started = await Flying.StartAsync(client, Project, parcel.Id, "build");
        await executor.Spawned.WaitAsync(TimeSpan.FromSeconds(15));
        Task joined = advancer.RequestAdvance(Project, started.Id);
        Task again = advancer.RequestAdvance(Project, started.Id);
        executor.Open();
        await Task.WhenAll(joined, again).WaitAsync(TimeSpan.FromSeconds(15));

        FlightView flight = await Flying.UntilAsync(client, Project, started.Id, "ended");
        executor.Inner.Spawns.Count(s => s.Agent == "build").Should().Be(1);
        FlightStepView build = flight.Steps.Should().ContainSingle(s => s.Step == "build").Subject;
        build.Answer.Should().NotBeNull();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_with_no_answer_parks_its_flight_naming_the_agent_while_another_flight_reaches_its_end()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView idea = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "idea", "Think first", null), None);
        ParcelView task = await Flying.TaskAtBuildAsync(client, Project, "Build anyway");

        FlightView discovery = await Flying.StartAsync(client, Project, idea.Id, "discovery");
        FlightView build = await Flying.StartAsync(client, Project, task.Id, "build");

        FlightView parked = await Flying.UntilAsync(client, Project, discovery.Id, "parked");
        parked.ParkReason.Should().StartWith("Step 'discover' got no judgment from agent 'discovery':").And.Contain("agent 'discovery'");
        parked.Lever.Should().Be(FlightLevers.PerformAgain("discover"));
        parked.Steps.Should().ContainSingle().Which.Answer.Should().BeNull();
        (await Flying.UntilAsync(client, Project, build.Id, "ended")).Outcome.Should().Be("pr-opened");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_judgment_past_the_wall_clock_limit_is_stopped_and_parks_its_flight_with_no_answer()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(Scripts.CleanBuildExecutor());
        var options = new DaemonOptions { Executor = executor, JudgmentLimit = TimeSpan.FromMilliseconds(300) };
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, options, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flying.TaskAtBuildAsync(client, Project, "Too slow");

        FlightView started = await Flying.StartAsync(client, Project, parcel.Id, "build");
        FlightView flight = await Flying.UntilAsync(client, Project, started.Id, "parked");

        flight.ParkReason.Should().Be("Step 'build' ran past the wall-clock limit of 0.3 seconds for one judgment, so its session was stopped.");
        flight.Lever.Should().Be(FlightLevers.PerformAgain("build"));
        flight.Steps[^1].Step.Should().Be("build");
        flight.Steps[^1].Answer.Should().BeNull();
        executor.Inner.Stops.Should().ContainSingle().Which.Agent.Should().Be("build");
        executor.StopTokensCancelled.Should().Equal(false);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_daemon_stop_cancels_the_judgment_in_progress_recording_nothing_and_the_next_start_carries_the_flight_on()
    {
        using var temp = new TemporaryHome();
        Guid flightId;
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        await using (RunningDaemon first = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = stalled }, None))
        {
            using var client = new DaemonClient(temp.Home);
            ParcelView parcel = await Flying.TaskAtBuildAsync(client, Project, "Interrupted");
            flightId = (await Flying.StartAsync(client, Project, parcel.Id, "build")).Id;
            await stalled.Spawned.WaitAsync(TimeSpan.FromSeconds(15));
            await first.StopAsync(None);
        }

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            Flight? between = await new FlightService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flightId, None);
            between.Should().NotBeNull();
            between.Status.Should().Be(FlightStatus.Running, "a daemon stop is not a park");
            between.CurrentStep.Should().Be("build");
            between.Current?.IsRecorded.Should().BeFalse("nothing is recorded for a step the stop cancelled");
        }

        await using RunningDaemon second = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var again = new DaemonClient(temp.Home);
        FlightView ended = await Flying.UntilAsync(again, Project, flightId, "ended");
        ended.Steps.Select(s => s.Step).Should().Equal(CleanPath);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_step_recorded_before_a_stop_is_routed_on_the_next_start_without_being_performed_again()
    {
        using var temp = new TemporaryHome();
        Guid flightId;
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinition.All))
        {
            var parcels = new Coanda.Daemon.Parcels.ParcelService(store, TimeProvider.System);
            var flights = new FlightService(store, TimeProvider.System);
            var parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Half done", null, None);
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            Flight flight = await flights.StartAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            await flights.RecordResultAsync(
                Principal.LocalOwner, Project, flight.Id, "worktree", SoftwareActions.Worktree, JsonSerializer.SerializeToElement(new { verdict = "done" }), None);
            await flights.StartStepAsync(Principal.LocalOwner, Project, flight.Id, "build", None);
            await flights.RecordAnswerAsync(
                Principal.LocalOwner, Project, flight.Id, "build", "build", ShippedAgents.Build.Hash, ShippedAgents.Build.Model,
                JsonSerializer.SerializeToElement(new { verdict = "done" }), Scripts.Usage(1), None);
            flightId = flight.Id;
        }

        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["reviewer-adversarial"] = [Scripts.Answer("clean", 2)],
            ["reviewer-conformance"] = [Scripts.Answer("clean", 3)],
        });
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        using var client = new DaemonClient(temp.Home);

        FlightView ended = await Flying.UntilAsync(client, Project, flightId, "ended");
        ended.Steps.Select(s => s.Step).Should().Equal(CleanPath);
        executor.Spawns.Select(s => s.Agent).Should().Equal("reviewer-adversarial", "reviewer-conformance");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_flight_stopped_while_its_judgment_runs_records_nothing_more_and_other_flights_still_advance()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1), Scripts.Answer("done", 2)],
            ["reviewer-adversarial"] = [Scripts.Answer("clean", 3)],
            ["reviewer-conformance"] = [Scripts.Answer("clean", 4)],
        }));
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        using var client = new DaemonClient(temp.Home);
        FlightService flights = daemon.Services.GetRequiredService<FlightService>();
        ParcelView first = await Flying.TaskAtBuildAsync(client, Project, "Called off");

        FlightView doomed = await Flying.StartAsync(client, Project, first.Id, "build");
        await executor.Spawned.WaitAsync(TimeSpan.FromSeconds(15));
        await flights.StopAsync(Principal.LocalOwner, Project, doomed.Id, "Not needed any more.", None);
        executor.Open();
        await Flying.UntilAsync(() => executor.Inner.Stops.Count == 1, "the stopped flight's session is ended");

        ParcelView second = await Flying.TaskAtBuildAsync(client, Project, "Still wanted");
        FlightView other = await Flying.StartAsync(client, Project, second.Id, "build");
        (await Flying.UntilAsync(client, Project, other.Id, "ended")).Outcome.Should().Be("pr-opened");

        FlightView stopped = await Flying.ShowAsync(client, Project, doomed.Id);
        stopped.Status.Should().Be("stopped");
        stopped.Steps.Select(s => s.Step).Should().Equal("worktree", "build");
        stopped.Steps[^1].Answer.Should().BeNull();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_refused_start_answers_with_the_rule()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView idea = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "idea", "Not a task", null), None);

        await FluentActions.Awaiting(() => Flying.StartAsync(client, Project, idea.Id, "build"))
            .Should().ThrowAsync<DomainException>().WithMessage("Flow 'build' accepts parcels of type task, not 'idea'.");

        await daemon.StopAsync(None);
    }

    private static void ExpectJudgment(FlightStepView step, AgentDefinition agent, int ordinal)
    {
        step.Kind.Should().Be("judgment");
        step.Performer.Should().Be(agent.Name);
        step.AgentHash.Should().Be(agent.Hash);
        step.Model.Should().Be(agent.Model);
        step.Measurement.Should().Be(Scripts.Usage(ordinal));
    }
}
