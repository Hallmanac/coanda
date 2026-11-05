using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Modules.Software.Steps;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Modules.Software;
using Coanda.Tests.Modules.Software.GitHub;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.SystemSteps;

/// <summary>
/// The daemon's one place a system step is dispatched: the core's steps, implemented in the daemon and
/// run here on a clock of the test's own, and the software module's, dispatched through the daemon's
/// host adapter for a project the module is switched on for.
/// </summary>
public sealed class FlowSystemStepsTests : IAsyncLifetime
{
    private const string Project = SoftwareNode.Project;
    private static readonly Principal FlowRunPrincipal = new("flow-run:under-test", Role.Agent);

    private readonly RecordingClock clock = new();
    private readonly ScriptedCommandRunner runner = new();
    private readonly FakeGitHub github = new();
    private SoftwareNode node = null!;
    private ParcelService parcels = null!;
    private FlowSystemSteps core = null!;
    private FlowSystemSteps systemSteps = null!;

    public async Task InitializeAsync()
    {
        node = await SoftwareNode.StartAsync(runner, github);
        parcels = node.Parcels;
        core = new FlowSystemSteps(FlowSystemSteps.Core(parcels, clock));
        systemSteps = node.Services.GetRequiredService<FlowSystemSteps>();
    }

    public async Task DisposeAsync() => await node.DisposeAsync();

    [Fact]
    public async Task The_names_a_flow_may_name_are_the_core_steps_and_the_software_modules_together()
    {
        string[] expected = [.. CoreSystemSteps.All, .. SoftwareSteps.All];
        Assert.Equivalent(expected, await systemSteps.NamesAsync(Project, CancellationToken.None), strict: true);
        Assert.Equivalent(CoreSystemSteps.All, systemSteps.Names, strict: true);
    }

    [Fact]
    public async Task Every_shipped_step_answers_unknown_to_a_status_and_does_nothing_on_a_stop()
    {
        foreach (string name in await systemSteps.NamesAsync(Project, CancellationToken.None))
        {
            Guid dispatchId = Ids.New();
            Assert.IsType<SystemStepStatus.Unknown>(await systemSteps.StatusAsync(Project, Step(name), name, dispatchId, CancellationToken.None));
            await systemSteps.StopAsync(name, dispatchId, CancellationToken.None);
        }

        Assert.Empty(runner.Commands);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public void Registering_two_implementations_of_one_name_is_refused()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => new FlowSystemSteps([new PlaceholderSystemStep("gates"), new PlaceholderSystemStep("gates")]));
        Assert.Contains("'gates' is registered twice", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Move_parcel_moves_the_parcel_as_the_flow_runs_principal_and_answers_done()
    {
        Parcel parcel = await TaskAtAsync("build");

        SystemStepAnswer outcome = await DispatchAsync(Step(CoreSystemSteps.MoveParcel, ("stage", FlowValue.Of("review"))), parcel.Id);

        JsonElement result = DoneResult(outcome);
        Assert.True(result.GetProperty("moved").GetBoolean());
        Parcel? moved = await parcels.ShowAsync(Principal.LocalOwner, Project, parcel.Id, CancellationToken.None);
        Assert.Equal("review", moved?.Stage);
        ParcelMoved recorded = Assert.Single(await MovesAsync(parcel.Id), m => m.ToStage == "review");
        Assert.Equal(FlowRunPrincipal.Id, recorded.MovedBy);
    }

    [Fact]
    public async Task Move_parcel_answers_done_without_a_move_when_the_parcel_is_already_there()
    {
        Parcel parcel = await TaskAtAsync("build");

        SystemStepAnswer outcome = await DispatchAsync(Step(CoreSystemSteps.MoveParcel, ("stage", FlowValue.Of("build"))), parcel.Id);

        Assert.False(DoneResult(outcome).GetProperty("moved").GetBoolean());
        // only the arrange step's move to build is recorded
        Assert.Single(await MovesAsync(parcel.Id));
    }

    [Fact]
    public async Task Move_parcel_pauses_quoting_the_rule_when_the_type_refuses_the_move()
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Still refining", null, CancellationToken.None);

        SystemStepAnswer outcome = await DispatchAsync(Step(CoreSystemSteps.MoveParcel, ("stage", FlowValue.Of("review"))), parcel.Id);

        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(outcome);
        Assert.StartsWith("Step 'act' could not move parcel", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("does not allow a move from 'refine' to 'review'", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("act"), paused.Lever);
    }

    [Fact]
    public async Task Timer_waits_its_seconds_on_the_daemons_clock_and_answers_done()
    {
        SystemStepAnswer outcome = await DispatchAsync(Step(CoreSystemSteps.Timer, ("seconds", FlowValue.Of(2.5))), Ids.New());

        Assert.Equal(2.5, DoneResult(outcome).GetProperty("seconds").GetDouble());
        Assert.Equal([TimeSpan.FromSeconds(2.5)], clock.Waits);
    }

    [Fact]
    public async Task Wait_for_human_pauses_with_its_lever_argument_and_answers_done_once_resumed()
    {
        FlowStep step = Step(CoreSystemSteps.WaitForHuman, ("lever", FlowValue.Of("Approve the plan in the journal.")));

        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(await DispatchAsync(step, Ids.New()));
        Assert.Contains("Step 'act'", paused.Reason, StringComparison.Ordinal);
        Assert.Equal("Approve the plan in the journal.", paused.Lever);

        DoneResult(await DispatchAsync(step, Ids.New(), resumed: true));
    }

    [Fact]
    public async Task Wait_for_external_event_pauses_naming_its_event_and_answers_done_once_resumed()
    {
        FlowStep step = Step(CoreSystemSteps.WaitForExternalEvent, ("event", FlowValue.Of("ci-finished")));

        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(await DispatchAsync(step, Ids.New()));
        Assert.Contains("'ci-finished'", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("Step 'act'", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("act"), paused.Lever);

        Assert.Equal("ci-finished", DoneResult(await DispatchAsync(step, Ids.New(), resumed: true)).GetProperty("event").GetString());
    }

    [Fact]
    public async Task Emit_outcome_answers_done_with_the_flows_declared_outcome()
    {
        SystemStepAnswer outcome = await DispatchAsync(Step(CoreSystemSteps.EmitOutcome), Ids.New());

        Assert.Equal(SoftwareFlows.Build.Outcome, DoneResult(outcome).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Worktree_answers_done_with_the_parcels_branch_and_the_head_it_checked_out()
    {
        Guid parcelId = (await TaskAtAsync("refine")).Id;

        JsonElement result = DoneResult(await DispatchAsync(Step(SoftwareSteps.Worktree), parcelId));

        Assert.Equal(SoftwareSteps.Worktree, result.GetProperty("systemStep").GetString());
        Assert.Equal($"coanda/{Ids.Short(parcelId)}", result.GetProperty("branch").GetString());
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, result.GetProperty("head").GetString());
        Assert.False(result.TryGetProperty("placeholder", out _));
    }

    [Fact]
    public async Task Gates_answers_done_once_the_build_and_test_commands_pass()
    {
        JsonElement result = DoneResult(await DispatchAsync(Step(SoftwareSteps.Gates), Ids.New()));

        Assert.Equal(SoftwareSteps.Gates, result.GetProperty("systemStep").GetString());
        Assert.False(result.TryGetProperty("placeholder", out _));
        Assert.Equal([SoftwareSettings.DefaultBuild, SoftwareSettings.DefaultTest], runner.ShellLines);
    }

    [Fact]
    public async Task Open_pr_answers_opened_with_the_pull_requests_number_and_url_and_the_head_it_pushed()
    {
        Parcel parcel = await TaskAtAsync("refine");
        (Guid flowRunId, Guid dispatchId) = await node.HeldAsync(parcel.Id, "open-pr");

        JsonElement result = Assert.IsType<SystemStepAnswer.Done>(await DispatchAsync(OpenPrFlowStep, parcel.Id, flowRunId: flowRunId, dispatchId: dispatchId)).Result;

        Assert.Equal(OpenPrStep.OpenedVerdict, result.GetProperty("verdict").GetString());
        Assert.Equal(SoftwareSteps.OpenPr, result.GetProperty("systemStep").GetString());
        Assert.Equal(FakeGitHub.Number, result.GetProperty("number").GetInt32());
        Assert.Equal(FakeGitHub.Url, result.GetProperty("url").GetString());
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, result.GetProperty("head").GetString());
        Assert.False(result.TryGetProperty("placeholder", out _));
    }

    [Theory]
    [InlineData(CoreSystemSteps.DeriveParcels)]
    public async Task A_placeholder_answers_done_naming_itself_as_a_placeholder(string systemStep)
    {
        SystemStepAnswer outcome = await DispatchAsync(Step(systemStep), Ids.New());

        JsonElement result = DoneResult(outcome);
        Assert.Equal(systemStep, result.GetProperty("systemStep").GetString());
        Assert.True(result.GetProperty("placeholder").GetBoolean());
    }

    [Theory]
    [InlineData(CoreSystemSteps.MoveParcel, "stage", null)]
    [InlineData(CoreSystemSteps.MoveParcel, "stage", 3.0)]
    [InlineData(CoreSystemSteps.Timer, "seconds", null)]
    [InlineData(CoreSystemSteps.Timer, "seconds", "ten")]
    [InlineData(CoreSystemSteps.Timer, "seconds", -1.0)]
    [InlineData(CoreSystemSteps.WaitForHuman, "lever", null)]
    [InlineData(CoreSystemSteps.WaitForHuman, "lever", true)]
    [InlineData(CoreSystemSteps.WaitForExternalEvent, "event", null)]
    [InlineData(CoreSystemSteps.WaitForExternalEvent, "event", 1.0)]
    public async Task A_missing_or_ill_kinded_argument_pauses_naming_the_step_and_the_argument(string systemStep, string argument, object? value)
    {
        FlowStep step = value is null ? Step(systemStep) : Step(systemStep, (argument, JsonSerializer.SerializeToElement(value)));

        SystemStepAnswer outcome = await DispatchAsync(step, Ids.New());

        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(outcome);
        Assert.Contains("Step 'act'", paused.Reason, StringComparison.Ordinal);
        Assert.Contains($"argument '{argument}'", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("act"), paused.Lever);
        Assert.Empty(clock.Waits);
    }

    private async Task<Parcel> TaskAtAsync(string stage)
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Act on me", null, CancellationToken.None);
        await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: CancellationToken.None);
        return stage == parcel.Stage
            ? parcel
            : await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, stage, CancellationToken.None);
    }

    private async Task<IReadOnlyList<ParcelMoved>> MovesAsync(Guid parcelId)
    {
        await using IQuerySession session = node.Store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: CancellationToken.None);
        return [.. events.Select(e => e.Data).OfType<ParcelMoved>()];
    }

    /// <summary>Dispatches a core step on the test's clock, and a module's step through the daemon.</summary>
    private ValueTask<SystemStepAnswer> DispatchAsync(FlowStep step, Guid parcelId, bool resumed = false, Guid? flowRunId = null, Guid? dispatchId = null)
    {
        Guid flowRun = flowRunId ?? Ids.New();
        var request = new SystemStepDispatch(
            FlowRunPrincipal, Project, flowRun, parcelId, SoftwareFlows.Build, step, resumed, Visit: 1, DispatchId: dispatchId ?? Ids.New(), WorkPath: node.Home.FlowRunWorkPath(Project, flowRun));
        return (CoreSystemSteps.All.Contains(step.SystemStep) ? core : systemSteps).DispatchAsync(request, CancellationToken.None);
    }

    private static JsonElement DoneResult(SystemStepAnswer outcome)
    {
        JsonElement result = Assert.IsType<SystemStepAnswer.Done>(outcome).Result;
        Assert.Equal("done", result.GetProperty("verdict").GetString());
        return result;
    }

    private static FlowStep OpenPrFlowStep => Step(SoftwareSteps.OpenPr) with
    {
        Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of(OpenPrStep.OpenedVerdict), VisitCap = 1, Target = Flow.End }],
    };

    private static FlowStep Step(string systemStep, params (string Name, JsonElement Value)[] arguments) => new()
    {
        Name = "act",
        SystemStep = systemStep,
        Arguments = arguments.ToDictionary(a => a.Name, a => a.Value),
        Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
    };

    /// <summary>The system clock, except that every timer it creates is recorded and fires at once.</summary>
    private sealed class RecordingClock : TimeProvider
    {
        private readonly Lock gate = new();
        private readonly List<TimeSpan> waits = [];

        public IReadOnlyList<TimeSpan> Waits
        {
            get
            {
                lock (gate)
                {
                    return [.. waits];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                waits.Add(dueTime);
            }

            return System.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }
}
