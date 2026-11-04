using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Actions;
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

namespace Coanda.Tests.Daemon.Actions;

public sealed class FlowActionsTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly Principal FlowRunPrincipal = new("flow-run:under-test", Role.Agent);

    private readonly TemporaryHome temp = new();
    private readonly RecordingClock clock = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private readonly ScriptedCommandRunner runner = new();
    private readonly FakeGitHub github = new();
    private FlowActions actions = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
        parcels = new ParcelService(store, TimeProvider.System);
        var projects = new ProjectService(store, TimeProvider.System);
        var clones = new ProjectClones(temp.Home, runner, NullLogger<ProjectClones>.Instance);
        actions = FlowActions.Shipped(parcels, projects, clones, runner, temp.Home, clock, DaemonOptions.DefaultGatesCommandLimit, github.Client,
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
        actions.Names.Should().BeEquivalentTo([.. CoreActions.All, .. SoftwareActions.All]);
    }

    [Fact]
    public void Registering_two_implementations_of_one_name_is_refused()
    {
        FluentActions.Invoking(() => new FlowActions([new PlaceholderAction("gates"), new PlaceholderAction("gates")]))
            .Should().Throw<InvalidOperationException>().WithMessage("*'gates' is registered twice*");
    }

    [Fact]
    public async Task Move_parcel_moves_the_parcel_as_the_flow_runs_principal_and_answers_done()
    {
        Parcel parcel = await TaskAtAsync("build");

        ActionOutcome outcome = await PerformAsync(Step(CoreActions.MoveParcel, ("state", FlowValue.Of("review"))), parcel.Id);

        JsonElement result = DoneResult(outcome);
        result.GetProperty("moved").GetBoolean().Should().BeTrue();
        Parcel? moved = await parcels.ShowAsync(Principal.LocalOwner, Project, parcel.Id, CancellationToken.None);
        moved?.State.Should().Be("review");
        ParcelMoved recorded = (await MovesAsync(parcel.Id)).Should().ContainSingle(m => m.ToState == "review").Subject;
        recorded.MovedBy.Should().Be(FlowRunPrincipal.Id);
    }

    [Fact]
    public async Task Move_parcel_answers_done_without_a_move_when_the_parcel_is_already_there()
    {
        Parcel parcel = await TaskAtAsync("build");

        ActionOutcome outcome = await PerformAsync(Step(CoreActions.MoveParcel, ("state", FlowValue.Of("build"))), parcel.Id);

        DoneResult(outcome).GetProperty("moved").GetBoolean().Should().BeFalse();
        (await MovesAsync(parcel.Id)).Should().ContainSingle("only the arrange step's move to build is recorded");
    }

    [Fact]
    public async Task Move_parcel_pauses_quoting_the_rule_when_the_type_refuses_the_move()
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Still refining", null, CancellationToken.None);

        ActionOutcome outcome = await PerformAsync(Step(CoreActions.MoveParcel, ("state", FlowValue.Of("review"))), parcel.Id);

        ActionOutcome.Paused paused = outcome.Should().BeOfType<ActionOutcome.Paused>().Subject;
        paused.Reason.Should().StartWith("Step 'act' could not move parcel").And.Contain("does not allow a move from 'refine' to 'review'");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("act"));
    }

    [Fact]
    public async Task Timer_waits_its_seconds_on_the_daemons_clock_and_answers_done()
    {
        ActionOutcome outcome = await PerformAsync(Step(CoreActions.Timer, ("seconds", FlowValue.Of(2.5))), Ids.New());

        DoneResult(outcome).GetProperty("seconds").GetDouble().Should().Be(2.5);
        clock.Waits.Should().Equal(TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task Wait_for_human_pauses_with_its_lever_argument_and_answers_done_once_resumed()
    {
        FlowStep step = Step(CoreActions.WaitForHuman, ("lever", FlowValue.Of("Approve the plan in the journal.")));

        ActionOutcome.Paused paused = (await PerformAsync(step, Ids.New())).Should().BeOfType<ActionOutcome.Paused>().Subject;
        paused.Reason.Should().Contain("Step 'act'");
        paused.Lever.Should().Be("Approve the plan in the journal.");

        DoneResult(await PerformAsync(step, Ids.New(), resumed: true));
    }

    [Fact]
    public async Task Wait_for_external_event_pauses_naming_its_event_and_answers_done_once_resumed()
    {
        FlowStep step = Step(CoreActions.WaitForExternalEvent, ("event", FlowValue.Of("ci-finished")));

        ActionOutcome.Paused paused = (await PerformAsync(step, Ids.New())).Should().BeOfType<ActionOutcome.Paused>().Subject;
        paused.Reason.Should().Contain("'ci-finished'").And.Contain("Step 'act'");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("act"));

        DoneResult(await PerformAsync(step, Ids.New(), resumed: true)).GetProperty("event").GetString().Should().Be("ci-finished");
    }

    [Fact]
    public async Task Emit_outcome_answers_done_with_the_flows_declared_outcome()
    {
        ActionOutcome outcome = await PerformAsync(Step(CoreActions.EmitOutcome), Ids.New());

        DoneResult(outcome).GetProperty("outcome").GetString().Should().Be(ShippedFlows.Build.Outcome);
    }

    [Fact]
    public async Task Worktree_answers_done_with_the_parcels_branch_and_the_head_it_checked_out()
    {
        Guid parcelId = Ids.New();

        JsonElement result = DoneResult(await PerformAsync(Step(SoftwareActions.Worktree), parcelId));

        result.GetProperty("action").GetString().Should().Be(SoftwareActions.Worktree);
        result.GetProperty("branch").GetString().Should().Be($"coanda/{Ids.Short(parcelId)}");
        result.GetProperty("head").GetString().Should().Be(ScriptedCommandRunner.PrimaryHead);
        result.TryGetProperty("placeholder", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Gates_answers_done_once_the_build_and_test_commands_pass()
    {
        JsonElement result = DoneResult(await PerformAsync(Step(SoftwareActions.Gates), Ids.New()));

        result.GetProperty("action").GetString().Should().Be(SoftwareActions.Gates);
        result.TryGetProperty("placeholder", out _).Should().BeFalse();
        runner.ShellLines.Should().Equal(Coanda.Core.Projects.Project.DefaultBuild, Coanda.Core.Projects.Project.DefaultTest);
    }

    [Fact]
    public async Task Open_pr_answers_opened_with_the_pull_requests_number_and_url_and_the_head_it_pushed()
    {
        Parcel parcel = await TaskAtAsync("build");

        JsonElement result = (await PerformAsync(OpenPrStep, parcel.Id)).Should().BeOfType<ActionOutcome.Done>().Subject.Result;

        result.GetProperty("verdict").GetString().Should().Be(OpenPrAction.OpenedVerdict);
        result.GetProperty("action").GetString().Should().Be(SoftwareActions.OpenPr);
        result.GetProperty("number").GetInt32().Should().Be(FakeGitHub.Number);
        result.GetProperty("url").GetString().Should().Be(FakeGitHub.Url);
        result.GetProperty("head").GetString().Should().Be(ScriptedCommandRunner.PrimaryHead);
        result.TryGetProperty("placeholder", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(CoreActions.DeriveParcels)]
    public async Task A_placeholder_answers_done_naming_itself_as_a_placeholder(string action)
    {
        ActionOutcome outcome = await PerformAsync(Step(action), Ids.New());

        JsonElement result = DoneResult(outcome);
        result.GetProperty("action").GetString().Should().Be(action);
        result.GetProperty("placeholder").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(CoreActions.MoveParcel, "state", null)]
    [InlineData(CoreActions.MoveParcel, "state", 3.0)]
    [InlineData(CoreActions.Timer, "seconds", null)]
    [InlineData(CoreActions.Timer, "seconds", "ten")]
    [InlineData(CoreActions.Timer, "seconds", -1.0)]
    [InlineData(CoreActions.WaitForHuman, "lever", null)]
    [InlineData(CoreActions.WaitForHuman, "lever", true)]
    [InlineData(CoreActions.WaitForExternalEvent, "event", null)]
    [InlineData(CoreActions.WaitForExternalEvent, "event", 1.0)]
    public async Task A_missing_or_ill_kinded_argument_pauses_naming_the_step_and_the_argument(string action, string argument, object? value)
    {
        FlowStep step = value is null ? Step(action) : Step(action, (argument, JsonSerializer.SerializeToElement(value)));

        ActionOutcome outcome = await PerformAsync(step, Ids.New());

        ActionOutcome.Paused paused = outcome.Should().BeOfType<ActionOutcome.Paused>().Subject;
        paused.Reason.Should().Contain("Step 'act'").And.Contain($"argument '{argument}'");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("act"));
        clock.Waits.Should().BeEmpty();
    }

    private async Task<Parcel> TaskAtAsync(string state)
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Act on me", null, CancellationToken.None);
        await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: CancellationToken.None);
        return state == parcel.State
            ? parcel
            : await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, state, CancellationToken.None);
    }

    private async Task<IReadOnlyList<ParcelMoved>> MovesAsync(Guid parcelId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: CancellationToken.None);
        return [.. events.Select(e => e.Data).OfType<ParcelMoved>()];
    }

    private ValueTask<ActionOutcome> PerformAsync(FlowStep step, Guid parcelId, bool resumed = false) =>
        actions.PerformAsync(
            new ActionRequest(FlowRunPrincipal, Project, Ids.New(), parcelId, ShippedFlows.Build, step, resumed),
            CancellationToken.None);

    private static JsonElement DoneResult(ActionOutcome outcome)
    {
        JsonElement result = outcome.Should().BeOfType<ActionOutcome.Done>().Subject.Result;
        result.GetProperty("verdict").GetString().Should().Be("done");
        return result;
    }

    private static FlowStep OpenPrStep => Step(SoftwareActions.OpenPr) with
    {
        Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of(OpenPrAction.OpenedVerdict), VisitCap = 1, Target = Flow.End }],
    };

    private static FlowStep Step(string action, params (string Name, JsonElement Value)[] arguments) => new()
    {
        Name = "act",
        Action = action,
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
