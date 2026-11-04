using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Api;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.GitHub;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coanda.Tests.Daemon.SystemSteps;

public sealed class FlowSystemStepsTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly Principal FlowRunPrincipal = new("flow-run:under-test", Role.Agent);

    private readonly TemporaryHome temp = new();
    private readonly RecordingClock clock = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private readonly ScriptedCommandRunner runner = new();
    private readonly FakeGitHub github = new();
    private FlowSystemSteps systemSteps = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
        parcels = new ParcelService(store, TimeProvider.System);
        var projects = new ProjectService(store, TimeProvider.System);
        var clones = new ProjectClones(temp.Home, runner, NullLogger<ProjectClones>.Instance);
        systemSteps = FlowSystemSteps.Shipped(parcels, projects, clones, runner, temp.Home, clock, DaemonOptions.DefaultGatesCommandLimit, github.Client,
            new FlowRunService(store, TimeProvider.System), new FlowRunWorkers());
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public void The_registered_names_are_the_core_and_software_catalogs_together()
    {
        string[] expected = [.. CoreSystemSteps.All, .. SoftwareSystemSteps.All];
        Assert.Equivalent(expected, systemSteps.Names, strict: true);
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

        SystemStepOutcome outcome = await PerformAsync(Step(CoreSystemSteps.MoveParcel, ("stage", FlowValue.Of("review"))), parcel.Id);

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

        SystemStepOutcome outcome = await PerformAsync(Step(CoreSystemSteps.MoveParcel, ("stage", FlowValue.Of("build"))), parcel.Id);

        Assert.False(DoneResult(outcome).GetProperty("moved").GetBoolean());
        // only the arrange step's move to build is recorded
        Assert.Single(await MovesAsync(parcel.Id));
    }

    [Fact]
    public async Task Move_parcel_pauses_quoting_the_rule_when_the_type_refuses_the_move()
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Still refining", null, CancellationToken.None);

        SystemStepOutcome outcome = await PerformAsync(Step(CoreSystemSteps.MoveParcel, ("stage", FlowValue.Of("review"))), parcel.Id);

        SystemStepOutcome.Paused paused = Assert.IsType<SystemStepOutcome.Paused>(outcome);
        Assert.StartsWith("Step 'act' could not move parcel", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("does not allow a move from 'refine' to 'review'", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("act"), paused.Lever);
    }

    [Fact]
    public async Task Timer_waits_its_seconds_on_the_daemons_clock_and_answers_done()
    {
        SystemStepOutcome outcome = await PerformAsync(Step(CoreSystemSteps.Timer, ("seconds", FlowValue.Of(2.5))), Ids.New());

        Assert.Equal(2.5, DoneResult(outcome).GetProperty("seconds").GetDouble());
        Assert.Equal([TimeSpan.FromSeconds(2.5)], clock.Waits);
    }

    [Fact]
    public async Task Wait_for_human_pauses_with_its_lever_argument_and_answers_done_once_resumed()
    {
        FlowStep step = Step(CoreSystemSteps.WaitForHuman, ("lever", FlowValue.Of("Approve the plan in the journal.")));

        SystemStepOutcome.Paused paused = Assert.IsType<SystemStepOutcome.Paused>(await PerformAsync(step, Ids.New()));
        Assert.Contains("Step 'act'", paused.Reason, StringComparison.Ordinal);
        Assert.Equal("Approve the plan in the journal.", paused.Lever);

        DoneResult(await PerformAsync(step, Ids.New(), resumed: true));
    }

    [Fact]
    public async Task Wait_for_external_event_pauses_naming_its_event_and_answers_done_once_resumed()
    {
        FlowStep step = Step(CoreSystemSteps.WaitForExternalEvent, ("event", FlowValue.Of("ci-finished")));

        SystemStepOutcome.Paused paused = Assert.IsType<SystemStepOutcome.Paused>(await PerformAsync(step, Ids.New()));
        Assert.Contains("'ci-finished'", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("Step 'act'", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("act"), paused.Lever);

        Assert.Equal("ci-finished", DoneResult(await PerformAsync(step, Ids.New(), resumed: true)).GetProperty("event").GetString());
    }

    [Fact]
    public async Task Emit_outcome_answers_done_with_the_flows_declared_outcome()
    {
        SystemStepOutcome outcome = await PerformAsync(Step(CoreSystemSteps.EmitOutcome), Ids.New());

        Assert.Equal(ShippedFlows.Build.Outcome, DoneResult(outcome).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Worktree_answers_done_with_the_parcels_branch_and_the_head_it_checked_out()
    {
        Guid parcelId = Ids.New();

        JsonElement result = DoneResult(await PerformAsync(Step(SoftwareSystemSteps.Worktree), parcelId));

        Assert.Equal(SoftwareSystemSteps.Worktree, result.GetProperty("systemStep").GetString());
        Assert.Equal($"coanda/{Ids.Short(parcelId)}", result.GetProperty("branch").GetString());
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, result.GetProperty("head").GetString());
        Assert.False(result.TryGetProperty("placeholder", out _));
    }

    [Fact]
    public async Task Gates_answers_done_once_the_build_and_test_commands_pass()
    {
        JsonElement result = DoneResult(await PerformAsync(Step(SoftwareSystemSteps.Gates), Ids.New()));

        Assert.Equal(SoftwareSystemSteps.Gates, result.GetProperty("systemStep").GetString());
        Assert.False(result.TryGetProperty("placeholder", out _));
        Assert.Equal([Coanda.Core.Projects.Project.DefaultBuild, Coanda.Core.Projects.Project.DefaultTest], runner.ShellLines);
    }

    [Fact]
    public async Task Open_pr_answers_opened_with_the_pull_requests_number_and_url_and_the_head_it_pushed()
    {
        Parcel parcel = await TaskAtAsync("build");

        JsonElement result = Assert.IsType<SystemStepOutcome.Done>(await PerformAsync(OpenPrStep, parcel.Id)).Result;

        Assert.Equal(OpenPrSystemStep.OpenedVerdict, result.GetProperty("verdict").GetString());
        Assert.Equal(SoftwareSystemSteps.OpenPr, result.GetProperty("systemStep").GetString());
        Assert.Equal(FakeGitHub.Number, result.GetProperty("number").GetInt32());
        Assert.Equal(FakeGitHub.Url, result.GetProperty("url").GetString());
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, result.GetProperty("head").GetString());
        Assert.False(result.TryGetProperty("placeholder", out _));
    }

    [Theory]
    [InlineData(CoreSystemSteps.DeriveParcels)]
    public async Task A_placeholder_answers_done_naming_itself_as_a_placeholder(string systemStep)
    {
        SystemStepOutcome outcome = await PerformAsync(Step(systemStep), Ids.New());

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

        SystemStepOutcome outcome = await PerformAsync(step, Ids.New());

        SystemStepOutcome.Paused paused = Assert.IsType<SystemStepOutcome.Paused>(outcome);
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
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: CancellationToken.None);
        return [.. events.Select(e => e.Data).OfType<ParcelMoved>()];
    }

    private ValueTask<SystemStepOutcome> PerformAsync(FlowStep step, Guid parcelId, bool resumed = false) =>
        systemSteps.PerformAsync(
            new SystemStepRequest(FlowRunPrincipal, Project, Ids.New(), parcelId, ShippedFlows.Build, step, resumed),
            CancellationToken.None);

    private static JsonElement DoneResult(SystemStepOutcome outcome)
    {
        JsonElement result = Assert.IsType<SystemStepOutcome.Done>(outcome).Result;
        Assert.Equal("done", result.GetProperty("verdict").GetString());
        return result;
    }

    private static FlowStep OpenPrStep => Step(SoftwareSystemSteps.OpenPr) with
    {
        Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of(OpenPrSystemStep.OpenedVerdict), VisitCap = 1, Target = Flow.End }],
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
