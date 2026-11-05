using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.SystemSteps;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.SystemSteps;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// A system step dispatched under a dispatch id the flow run records before the call: a step that
/// answers running leaves the flow run running with no worker until its outcome is recorded under
/// that id; a person's pause holds the dispatch and a resume stops it before dispatching under a new
/// id; a person's stop stops it; and a daemon restart asks its status rather than dispatching blindly.
/// </summary>
public sealed class DispatchFlowRunTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private static readonly Flow Dispatching = RunningSystemStep.FlowNamed();

    [Fact]
    public async Task A_step_finds_the_dispatch_id_it_was_given_recorded_on_the_flow_run_while_it_runs()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        await using RunningDaemon daemon = await StartDaemonAsync(temp, step);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();
        Guid? seen = null;
        step.Answer = async (request, token) =>
        {
            FlowRun? during = await flowRuns.ShowAsync(Principal.LocalOwner, request.Project, request.FlowRunId, token);
            seen = during?.Current?.DispatchId;
            return RunningSystemStep.Done();
        };
        using var client = new DaemonClient(temp.Home);
        (Guid flowRunId, Guid parcelId) = await StartFlowRunAsync(daemon, client);

        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        SystemStepDispatch request = Assert.Single(step.Dispatches);
        Assert.NotEqual(Guid.Empty, request.DispatchId);
        Assert.Equal(request.DispatchId, seen);
        Assert.Equal(Project, request.Project);
        Assert.Equal(parcelId, request.ParcelId);
        Assert.Equal(flowRunId, request.FlowRunId);
        Assert.Equal("work", request.Step.Name);
        Assert.Equal(RunningSystemStep.StepName, request.SystemStep);
        Assert.Equal(1, request.Visit);
        Assert.False(request.Resumed);
        Assert.Equal(temp.Home.FlowRunWorkPath(Project, flowRunId), request.WorkPath);
        Assert.Equal("as the author wrote it", request.Arguments["note"].GetString());
        Assert.Equal("test", Assert.Single(ended.Steps).Result.GetValueOrDefault().GetProperty("from").GetString());

        IReadOnlyList<object> events = await EventsOfAsync(daemon, flowRunId);
        Assert.Equal(request.DispatchId, Assert.Single(events.OfType<SystemStepDispatched>()).DispatchId);
        // the result is recorded as the outcome of its dispatch
        Assert.Equal(request.DispatchId, Assert.Single(events.OfType<SystemStepCompleted>()).DispatchId);
        Assert.Empty(step.Statuses);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_dispatch_that_answers_running_leaves_the_flow_run_running_with_no_worker_until_its_recorded_outcome_is_routed()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        await using RunningDaemon daemon = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        Guid parcelId = await PublishedParcelAsync(daemon, client);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcelId, Dispatching.Name, None);

        // The advance requested for the flow run completes once its step answers running.
        await daemon.Services.GetRequiredService<IFlowRunAdvancer>().RequestAdvance(Project, started.Id).WaitAsync(Patience);

        Assert.True(daemon.Services.GetRequiredService<FlowRunWorkers>().ReturnedAsync(started.Id, None).IsCompleted);
        SystemStepDispatch dispatched = Assert.Single(step.Dispatches);
        FlowRun running = await ShowAsync(daemon, started.Id);
        Assert.Equal(FlowRunStatus.Running, running.Status);
        Assert.Null(running.PauseReason);
        Assert.Null(running.Lever);
        Assert.Equal(dispatched.DispatchId, running.Current?.DispatchId);
        Assert.True(running.Current?.AwaitsDispatch);

        await flowRuns.RecordOutcomeAsync(Principal.LocalOwner, Project, started.Id, dispatched.DispatchId, RunningSystemStep.Done(), None);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        Assert.Equal("dispatched", ended.Outcome);
        Assert.Equal("test", Assert.Single(ended.Steps).Result.GetValueOrDefault().GetProperty("from").GetString());
        Assert.Single(step.Dispatches);
        Assert.Empty(step.Statuses);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_running_dispatchs_paused_outcome_pauses_the_flow_run_with_its_reason_and_lever()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        await using RunningDaemon daemon = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        (Guid flowRunId, _) = await StartFlowRunAsync(daemon, client);
        Guid dispatchId = await AnsweredRunningAsync(daemon, step, flowRunId);

        await daemon.Services.GetRequiredService<FlowRunService>().RecordOutcomeAsync(
            Principal.LocalOwner, Project, flowRunId, dispatchId, new SystemStepAnswer.Paused("The work needs a person.", "Look, then resume."), None);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        Assert.Equal("The work needs a person.", paused.PauseReason);
        Assert.Equal("Look, then resume.", paused.Lever);
        Assert.Equal(dispatchId, Assert.Single((await EventsOfAsync(daemon, flowRunId)).OfType<FlowRunPaused>()).DispatchId);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_outcome_saved_before_the_dispatch_call_returns_running_is_routed()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        await using RunningDaemon daemon = await StartDaemonAsync(temp, step);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();
        step.Answer = async (request, token) =>
        {
            await flowRuns.RecordOutcomeAsync(Principal.LocalOwner, request.Project, request.FlowRunId, request.DispatchId, RunningSystemStep.Done(), token);
            return new SystemStepAnswer.Running();
        };
        using var client = new DaemonClient(temp.Home);
        (Guid flowRunId, _) = await StartFlowRunAsync(daemon, client);

        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal("dispatched", ended.Outcome);
        Assert.Single(step.Dispatches);
        Assert.Empty(step.Statuses);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_persons_pause_holds_a_running_dispatch_refuses_its_outcome_and_the_resume_stops_it_before_dispatching_under_a_new_id()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        await using RunningDaemon daemon = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        (Guid flowRunId, _) = await StartFlowRunAsync(daemon, client);
        Guid held = await AnsweredRunningAsync(daemon, step, flowRunId);

        await FlowRunDriver.PauseAsync(client, Project, flowRunId, "Wait while I look.");
        FlowRun paused = await ShowAsync(daemon, flowRunId);
        // the person's pause is no outcome of the dispatch, which stays held
        Assert.True(paused.Current?.HoldsDispatch);
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => daemon.Services.GetRequiredService<FlowRunService>()
            .RecordOutcomeAsync(Principal.LocalOwner, Project, flowRunId, held, RunningSystemStep.Done(), None));
        Assert.Contains("is paused", refused.Message, StringComparison.Ordinal);

        int dispatchedAtStop = -1;
        step.OnStop = _ => dispatchedAtStop = step.Dispatches.Count;
        step.Answer = (_, _) => Task.FromResult<SystemStepAnswer>(RunningSystemStep.Done());
        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "Carry on.");
        await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal([held], step.Stops);
        // the held dispatch was stopped before the step was dispatched again
        Assert.Equal(1, dispatchedAtStop);
        Assert.Equal(2, step.Dispatches.Count);
        SystemStepDispatch again = step.Dispatches[1];
        Assert.NotEqual(held, again.DispatchId);
        Assert.True(again.Resumed);
        Assert.Equal(1, again.Visit);
        Assert.Equal(again.DispatchId, (await ShowAsync(daemon, flowRunId)).Current?.DispatchId);
        Assert.Empty(step.Statuses);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_persons_stop_of_a_flow_run_holding_a_running_dispatch_stops_it_before_the_worktree_is_removed_and_records_nothing()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        var step = new RunningSystemStep();
        await using RunningDaemon daemon = await StartDaemonAsync(temp, step, runner);
        using var client = new DaemonClient(temp.Home);
        (Guid flowRunId, _) = await StartFlowRunAsync(daemon, client);
        Guid held = await AnsweredRunningAsync(daemon, step, flowRunId);
        string clone = temp.Home.ProjectClonePath(Project);
        string workPath = temp.Home.FlowRunWorkPath(Project, flowRunId);
        Directory.CreateDirectory(clone);
        runner.AddWorktree(clone, workPath, "task/dispatched", ScriptedCommandRunner.PrimaryHead);
        bool worktreeAtStop = false;
        step.OnStop = _ => worktreeAtStop = runner.Worktrees(clone).Contains(workPath);

        await FlowRunDriver.StopAsync(client, Project, flowRunId, "Not needed.");
        await FlowRunDriver.UntilAsync(() => runner.Worktrees(clone).Count == 0, "the stopped flow run's worktree is removed");

        Assert.Equal([held], step.Stops);
        Assert.True(worktreeAtStop, "the dispatch is stopped before the worktree is removed");
        IReadOnlyList<object> events = await EventsOfAsync(daemon, flowRunId);
        Assert.Empty(events.OfType<SystemStepCompleted>());
        Assert.Empty(events.OfType<FlowRunPaused>());
        Assert.IsType<FlowRunStopped>(events[^1]);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_persons_stop_while_the_dispatch_call_is_in_progress_cancels_it_records_nothing_and_asks_the_stop_once()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        step.Answer = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new SystemStepAnswer.Running();
        };
        await using RunningDaemon daemon = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        (Guid flowRunId, _) = await StartFlowRunAsync(daemon, client);
        await entered.Task.WaitAsync(Patience);

        await FlowRunDriver.StopAsync(client, Project, flowRunId, "Not needed.");
        await daemon.Services.GetRequiredService<FlowRunWorkers>().ReturnedAsync(flowRunId, None).WaitAsync(Patience);

        // cancelling the call does not end a dispatch, so the step is told to stop it all the same
        Assert.Equal([Assert.Single(step.Dispatches).DispatchId], step.Stops);
        IReadOnlyList<object> events = await EventsOfAsync(daemon, flowRunId);
        Assert.Empty(events.OfType<SystemStepCompleted>());
        Assert.Empty(events.OfType<FlowRunPaused>());
        Assert.Equal("stopped", (await FlowRunDriver.ShowAsync(client, Project, flowRunId)).Status);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_dispatch_that_answers_running_as_a_persons_stop_arrives_is_told_to_stop()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        step.Answer = async (_, _) =>
        {
            // committed to its work, the step no longer heeds the call's token
            entered.TrySetResult();
            await release.Task;
            return new SystemStepAnswer.Running();
        };
        await using RunningDaemon daemon = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        (Guid flowRunId, _) = await StartFlowRunAsync(daemon, client);
        await entered.Task.WaitAsync(Patience);

        await FlowRunDriver.StopAsync(client, Project, flowRunId, "Not needed.");
        release.SetResult();
        await daemon.Services.GetRequiredService<FlowRunWorkers>().ReturnedAsync(flowRunId, None).WaitAsync(Patience);

        Assert.Equal([Assert.Single(step.Dispatches).DispatchId], step.Stops);
        IReadOnlyList<object> events = await EventsOfAsync(daemon, flowRunId);
        Assert.Empty(events.OfType<SystemStepCompleted>());
        Assert.IsType<FlowRunStopped>(events[^1]);

        await daemon.StopAsync(None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_persons_pause_and_resume_while_the_dispatch_call_is_in_progress_dispatches_again_under_a_new_id_and_does_not_pause(bool answersPaused)
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        step.Answer = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                return RunningSystemStep.Done();
            }

            entered.TrySetResult();
            await release.Task;
            return answersPaused
                ? new SystemStepAnswer.Paused("Waiting on the build.", "coa flow-run resume")
                : RunningSystemStep.Done();
        };
        await using RunningDaemon daemon = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        (Guid flowRunId, _) = await StartFlowRunAsync(daemon, client);
        await entered.Task.WaitAsync(Patience);
        Guid first = Assert.Single(step.Dispatches).DispatchId;

        await FlowRunDriver.PauseAsync(client, Project, flowRunId, "Wait while I look.");
        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "Carry on.");
        release.SetResult();
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal("dispatched", ended.Outcome);
        Assert.Equal(2, step.Dispatches.Count);
        Guid again = step.Dispatches[1].DispatchId;
        Assert.NotEqual(first, again);
        Assert.True(step.Dispatches[1].Resumed);
        // the replaced dispatch is stopped before the step is dispatched again
        Assert.Equal([first], step.Stops);
        IReadOnlyList<object> events = await EventsOfAsync(daemon, flowRunId);
        // the only pause is the person's own: the replaced dispatch's answer neither records nor pauses
        Assert.Equal("Wait while I look.", Assert.Single(events.OfType<FlowRunPaused>()).Reason);
        Assert.Equal((Guid?)again, Assert.Single(events.OfType<SystemStepCompleted>()).DispatchId);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_asks_the_status_of_a_running_dispatch_and_one_still_running_waits_for_its_outcome()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        (Guid flowRunId, Guid held) = await StopWhileRunningAsync(temp, step);

        await using RunningDaemon second = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        await FlowRunDriver.UntilAsync(() => step.Statuses.Count == 1, "the restart asks the dispatch's status");
        await second.Services.GetRequiredService<FlowRunWorkers>().ReturnedAsync(flowRunId, None).WaitAsync(Patience);

        Assert.Equal([held], step.Statuses);
        FlowRun waiting = await ShowAsync(second, flowRunId);
        Assert.Equal(FlowRunStatus.Running, waiting.Status);
        Assert.Null(waiting.PauseReason);
        Assert.Equal(held, waiting.Current?.DispatchId);

        await second.Services.GetRequiredService<FlowRunService>().RecordOutcomeAsync(Principal.LocalOwner, Project, flowRunId, held, RunningSystemStep.Done(), None);
        await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Single(step.Dispatches);
        Assert.Single((await EventsOfAsync(second, flowRunId)).OfType<SystemStepDispatched>());

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_records_and_routes_a_done_status_without_dispatching_again()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        (Guid flowRunId, Guid held) = await StopWhileRunningAsync(temp, step);
        step.Status = RunningSystemStep.Answering(new SystemStepStatus.Done(RunningSystemStep.Done().Result));

        await using RunningDaemon second = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal([held], step.Statuses);
        Assert.Single(step.Dispatches);
        Assert.Equal("test", Assert.Single(ended.Steps).Result.GetValueOrDefault().GetProperty("from").GetString());
        Assert.Equal(held, Assert.Single((await EventsOfAsync(second, flowRunId)).OfType<SystemStepCompleted>()).DispatchId);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_records_a_paused_status_as_its_dispatchs_outcome_without_dispatching_again()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        (Guid flowRunId, Guid held) = await StopWhileRunningAsync(temp, step);
        step.Status = RunningSystemStep.Answering(new SystemStepStatus.Paused("The work found a question.", "Answer it, then resume."));

        await using RunningDaemon second = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        Assert.Equal("The work found a question.", paused.PauseReason);
        Assert.Equal("Answer it, then resume.", paused.Lever);
        Assert.Equal([held], step.Statuses);
        Assert.Single(step.Dispatches);
        Assert.Equal(held, Assert.Single((await EventsOfAsync(second, flowRunId)).OfType<FlowRunPaused>()).DispatchId);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_dispatches_again_under_the_same_id_a_dispatch_whose_step_answers_unknown()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        (Guid flowRunId, Guid held) = await StopWhileRunningAsync(temp, step);
        step.Status = RunningSystemStep.Answering(new SystemStepStatus.Unknown());
        step.Answer = (_, _) => Task.FromResult<SystemStepAnswer>(RunningSystemStep.Done());

        await using RunningDaemon second = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal([held], step.Statuses);
        Assert.Equal([held, held], step.Dispatches.Select(d => d.DispatchId));
        // the dispatch again carries what the first one did
        Assert.Equal(
            (step.Dispatches[0].Visit, step.Dispatches[0].Resumed, step.Dispatches[0].WorkPath),
            (step.Dispatches[1].Visit, step.Dispatches[1].Resumed, step.Dispatches[1].WorkPath));
        IReadOnlyList<object> events = await EventsOfAsync(second, flowRunId);
        Assert.Single(events.OfType<SystemStepDispatched>());
        Assert.Equal(held, Assert.Single(events.OfType<SystemStepCompleted>()).DispatchId);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_whose_status_call_throws_pauses_with_the_lever_a_dispatch_that_throws_gets()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        (Guid flowRunId, Guid held) = await StopWhileRunningAsync(temp, step);
        step.Status = (_, _) => throw new IOException("The step's record could not be read.");

        await using RunningDaemon second = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        Assert.Equal(FlowRunLevers.PerformAgain("work"), paused.Lever);
        Assert.Equal(
            $"Step 'work' could not ask the system step '{RunningSystemStep.StepName}' where dispatch {Ids.Short(held)} stands: The step's record could not be read.",
            paused.PauseReason);
        Assert.Single(step.Dispatches);
        // a status that threw is no outcome, so the dispatch stays held for the resume to stop
        Assert.True((await ShowAsync(second, flowRunId)).Current?.HoldsDispatch);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_status_answer_the_outcome_command_recorded_first_is_routed_once_and_does_not_pause_the_flow_run()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        (Guid flowRunId, Guid held) = await StopWhileRunningAsync(temp, step);

        // The step records its outcome through the command while the restart's status call is in
        // progress, then answers the status with the same outcome.
        var daemonStarted = new TaskCompletionSource<RunningDaemon>(TaskCreationOptions.RunContinuationsAsynchronously);
        step.Status = async (dispatchId, token) =>
        {
            RunningDaemon running = await daemonStarted.Task.WaitAsync(token);
            await running.Services.GetRequiredService<FlowRunService>().RecordOutcomeAsync(
                Principal.LocalOwner, Project, flowRunId, dispatchId, RunningSystemStep.Done(), token);
            return new SystemStepStatus.Done(RunningSystemStep.Done().Result);
        };
        await using RunningDaemon second = await StartDaemonAsync(temp, step);
        daemonStarted.SetResult(second);
        using var client = new DaemonClient(temp.Home);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal([held], step.Statuses);
        Assert.Single(step.Dispatches);
        IReadOnlyList<object> events = await EventsOfAsync(second, flowRunId);
        Assert.Equal(held, Assert.Single(events.OfType<SystemStepCompleted>()).DispatchId);
        Assert.Empty(events.OfType<FlowRunPaused>());
        Assert.Equal("dispatched", ended.Outcome);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_persons_stop_during_a_status_call_still_stops_the_dispatch_it_asked_about()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        (Guid flowRunId, Guid held) = await StopWhileRunningAsync(temp, step);
        var asking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        step.Status = async (_, token) =>
        {
            asking.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new SystemStepStatus.Running();
        };

        await using RunningDaemon second = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        await asking.Task.WaitAsync(Patience);
        await FlowRunDriver.StopAsync(client, Project, flowRunId, "Not needed.");
        await FlowRunDriver.UntilAsync(() => step.Stops.Count == 1, "the held dispatch is stopped");

        Assert.Equal([held], step.Stops);
        IReadOnlyList<object> events = await EventsOfAsync(second, flowRunId);
        Assert.Empty(events.OfType<SystemStepCompleted>());
        Assert.Empty(events.OfType<FlowRunPaused>());

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_between_a_resume_and_its_new_dispatch_stops_the_held_dispatch_and_dispatches_under_a_new_id_without_asking_status()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        Guid flowRunId;
        Guid held;
        await using (RunningDaemon first = await StartDaemonAsync(temp, step))
        {
            using var client = new DaemonClient(temp.Home);
            (flowRunId, _) = await StartFlowRunAsync(first, client);
            held = await AnsweredRunningAsync(first, step, flowRunId);
            await FlowRunDriver.PauseAsync(client, Project, flowRunId, "Wait while I look.");
            await first.StopAsync(None);
        }

        // The resume is saved while no daemon runs, so no advance follows it before the restart.
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            FlowRun resumed = await new FlowRunService(store, TimeProvider.System).ResumeAsync(Principal.LocalOwner, Project, flowRunId, "Carry on.", None);
            Assert.Equal(held, resumed.Current?.DispatchId);
            Assert.True(resumed.Current?.ResumedSinceDispatch);
        }

        int dispatchedAtStop = -1;
        step.OnStop = _ => dispatchedAtStop = step.Dispatches.Count;
        step.Answer = (_, _) => Task.FromResult<SystemStepAnswer>(RunningSystemStep.Done());
        await using RunningDaemon second = await StartDaemonAsync(temp, step);
        using var again = new DaemonClient(temp.Home);
        await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");

        Assert.Empty(step.Statuses);
        Assert.Equal([held], step.Stops);
        Assert.Equal(1, dispatchedAtStop);
        SystemStepDispatch redispatched = step.Dispatches[^1];
        Assert.NotEqual(held, redispatched.DispatchId);
        Assert.True(redispatched.Resumed);
        IReadOnlyList<object> events = await EventsOfAsync(second, flowRunId);
        Assert.Equal([held, redispatched.DispatchId], events.OfType<SystemStepDispatched>().Select(e => e.DispatchId));

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_running_flow_run_whose_system_step_was_entered_before_dispatch_ids_is_dispatched_under_a_new_id_on_the_next_start()
    {
        using var temp = new TemporaryHome();
        var step = new RunningSystemStep { Answer = (_, _) => Task.FromResult<SystemStepAnswer>(RunningSystemStep.Done()) };
        Guid flowRunId;

        // The store holds only what a daemon without dispatch ids writes: a flow run started at a system step.
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All))
        {
            await RegisteredProjects.AddAsync(store, Project, None);
            var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: new HashSet<string>([.. ShippedSystemSteps.Names, step.Name]));
            Definition draft = await definitions.DraftAsync(
                Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, Dispatching.Name, Flow.CanonicalJson(Dispatching), Dispatching.Hash, null, None);
            await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
            var parcels = new ParcelService(store, TimeProvider.System);
            Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Entered before dispatch ids", null, None);
            FlowRun started = await new FlowRunService(store, TimeProvider.System).StartAsync(Principal.LocalOwner, Project, parcel.Id, Dispatching.Name, None);
            flowRunId = started.Id;

            await using IQuerySession session = store.QuerySession(Project);
            IReadOnlyList<IEvent> written = await session.Events.FetchStreamAsync(flowRunId, token: None);
            Assert.IsType<FlowRunStarted>(Assert.Single(written).Data);
            Assert.Null(started.Current?.DispatchId);
        }

        await using RunningDaemon daemon = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Empty(step.Statuses);
        SystemStepDispatch dispatched = Assert.Single(step.Dispatches);
        Assert.NotEqual(Guid.Empty, dispatched.DispatchId);
        Assert.Equal(dispatched.DispatchId, Assert.Single((await EventsOfAsync(daemon, flowRunId)).OfType<SystemStepDispatched>()).DispatchId);

        await daemon.StopAsync(None);
    }

    private static Task<RunningDaemon> StartDaemonAsync(TemporaryHome temp, RunningSystemStep step, ScriptedCommandRunner? runner = null) =>
        Daemons.StartAsync(temp.Home, new DaemonOptions { SystemSteps = [step], CommandRunner = runner }, None);

    /// <summary>Registers the project, publishes the flow naming the test step, and adds a task for it.</summary>
    private static async Task<Guid> PublishedParcelAsync(RunningDaemon daemon, DaemonClient client)
    {
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await Publishing.FlowAsync(daemon, Project, Dispatching);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Dispatched", null), None);
        return parcel.Id;
    }

    /// <summary>Starts a flow run of the flow naming the test step through the endpoint, which advances it.</summary>
    private static async Task<(Guid FlowRunId, Guid ParcelId)> StartFlowRunAsync(RunningDaemon daemon, DaemonClient client)
    {
        Guid parcelId = await PublishedParcelAsync(daemon, client);
        return ((await FlowRunDriver.StartAsync(client, Project, parcelId, Dispatching.Name)).Id, parcelId);
    }

    /// <summary>Waits for the step's first dispatch and for the worker to return from it, and gives the dispatch id the flow run holds.</summary>
    private static async Task<Guid> AnsweredRunningAsync(RunningDaemon daemon, RunningSystemStep step, Guid flowRunId)
    {
        await FlowRunDriver.UntilAsync(() => step.Dispatches.Count == 1, "the step is dispatched");
        await daemon.Services.GetRequiredService<FlowRunWorkers>().ReturnedAsync(flowRunId, None).WaitAsync(Patience);
        FlowRun running = await ShowAsync(daemon, flowRunId);
        Assert.Equal(FlowRunStatus.Running, running.Status);
        Guid held = running.Current?.DispatchId ?? throw new InvalidOperationException("The flow run holds no dispatch id.");
        Assert.Equal(step.Dispatches[0].DispatchId, held);
        return held;
    }

    /// <summary>Starts a flow run whose step answers running, stops the daemon, and gives the flow run and the dispatch it holds.</summary>
    private static async Task<(Guid FlowRunId, Guid DispatchId)> StopWhileRunningAsync(TemporaryHome temp, RunningSystemStep step)
    {
        await using RunningDaemon first = await StartDaemonAsync(temp, step);
        using var client = new DaemonClient(temp.Home);
        (Guid flowRunId, _) = await StartFlowRunAsync(first, client);
        Guid held = await AnsweredRunningAsync(first, step, flowRunId);
        await first.StopAsync(None);
        Assert.Empty(step.Statuses);
        return (flowRunId, held);
    }

    private static async Task<FlowRun> ShowAsync(RunningDaemon daemon, Guid flowRunId) =>
        await daemon.Services.GetRequiredService<FlowRunService>().ShowAsync(Principal.LocalOwner, Project, flowRunId, None)
            ?? throw new InvalidOperationException($"No flow run {flowRunId}.");

    private static async Task<IReadOnlyList<object>> EventsOfAsync(RunningDaemon daemon, Guid flowRunId)
    {
        await using IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(flowRunId, token: None);
        return [.. events.Select(e => e.Data)];
    }
}
