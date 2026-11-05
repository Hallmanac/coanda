using System.Text.Json;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.SystemSteps;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// The command that records a dispatch's outcome under its dispatch id, at the service with no
/// runner behind it: what it records, and each outcome it refuses.
/// </summary>
public sealed class DispatchOutcomeTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryHome temp = new();
    private IDocumentStore store = null!;
    private FlowRunService flowRuns = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, None);
        flowRuns = new FlowRunService(store, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public void The_outcome_command_is_open_to_the_roles_the_result_command_is_open_to_and_to_the_module_role()
    {
        RegistryEntry result = Assert.Single(CommandRegistry.Entries, e => e.Name == "flow-run.result");
        RegistryEntry outcome = Assert.Single(CommandRegistry.Entries, e => e.Name == "flow-run.outcome");

        Assert.Equal(EntryKind.Command, outcome.Kind);
        Assert.Equal([Role.Module], outcome.AllowedRoles.Except(result.AllowedRoles));
        Assert.Empty(result.AllowedRoles.Except(outcome.AllowedRoles));
    }

    [Fact]
    public async Task A_done_outcome_is_recorded_as_its_dispatchs_result()
    {
        (Guid flowRunId, Guid dispatchId) = await DispatchedAsync();

        FlowRun recorded = await flowRuns.RecordOutcomeAsync(Principal.LocalOwner, Project, flowRunId, dispatchId, Done(), None);

        Assert.Equal("done", recorded.Current?.Result?.GetProperty("verdict").GetString());
        Assert.NotNull(recorded.Current?.DispatchOutcomeAt);
        Assert.Equal(dispatchId, Assert.Single((await EventsOfAsync(flowRunId)).OfType<SystemStepCompleted>()).DispatchId);
    }

    [Fact]
    public async Task An_outcome_under_a_dispatch_id_that_is_not_the_current_visits_is_refused()
    {
        (Guid flowRunId, Guid dispatchId) = await DispatchedAsync();
        Guid other = Ids.New();

        DomainException refused = await RefusedAsync(flowRunId, other, Done());

        Assert.Equal(
            $"Step 'worktree' of flow run {Ids.Short(flowRunId)} holds dispatch {Ids.Short(dispatchId)}, not {Ids.Short(other)}; an outcome under dispatch {Ids.Short(other)} is not recorded.",
            refused.Message);
    }

    [Fact]
    public async Task An_outcome_under_a_dispatch_that_already_holds_one_is_refused()
    {
        (Guid flowRunId, Guid dispatchId) = await DispatchedAsync();
        await flowRuns.RecordOutcomeAsync(Principal.LocalOwner, Project, flowRunId, dispatchId, Done(), None);

        DomainException refused = await RefusedAsync(flowRunId, dispatchId, new SystemStepAnswer.Paused("Again.", "Resume."));

        Assert.Contains($"already holds the outcome of dispatch {Ids.Short(dispatchId)}", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_outcome_on_a_stopped_flow_run_is_refused()
    {
        (Guid flowRunId, Guid dispatchId) = await DispatchedAsync();
        await flowRuns.StopAsync(Principal.LocalOwner, Project, flowRunId, "Not needed.", None);

        DomainException refused = await RefusedAsync(flowRunId, dispatchId, Done());

        Assert.Equal($"Flow run {Ids.Short(flowRunId)} has been stopped; only a running flow run can record a dispatch's outcome.", refused.Message);
    }

    [Fact]
    public async Task An_outcome_on_a_paused_flow_run_is_refused()
    {
        (Guid flowRunId, Guid dispatchId) = await DispatchedAsync();
        await flowRuns.PauseAsync(Principal.LocalOwner, Project, flowRunId, "Wait.", "Resume when ready.", None);

        DomainException refused = await RefusedAsync(flowRunId, dispatchId, Done());

        Assert.Equal($"Flow run {Ids.Short(flowRunId)} is paused; only a running flow run can record a dispatch's outcome.", refused.Message);
    }

    [Fact]
    public async Task An_outcome_on_an_ended_flow_run_is_refused()
    {
        (Guid flowRunId, Guid dispatchId) = await DispatchedAsync();
        await flowRuns.EndAsync(Principal.LocalOwner, Project, flowRunId, SoftwareFlows.Build.Outcome, None);

        DomainException refused = await RefusedAsync(flowRunId, dispatchId, Done());

        Assert.Equal($"Flow run {Ids.Short(flowRunId)} has ended; only a running flow run can record a dispatch's outcome.", refused.Message);
    }

    [Fact]
    public async Task An_outcome_under_a_dispatch_a_resume_replaced_is_refused_before_and_after_the_new_dispatch()
    {
        (Guid flowRunId, Guid dispatchId) = await DispatchedAsync();
        await flowRuns.PauseAsync(Principal.LocalOwner, Project, flowRunId, "Wait.", "Resume when ready.", None);
        await flowRuns.ResumeAsync(Principal.LocalOwner, Project, flowRunId, null, None);

        DomainException beforeNew = await RefusedAsync(flowRunId, dispatchId, Done());
        FlowRun redispatched = await flowRuns.DispatchedAsync(Principal.LocalOwner, Project, flowRunId, "worktree", None);
        Guid replacement = redispatched.Current?.DispatchId ?? throw new InvalidOperationException("No new dispatch was recorded.");
        DomainException afterNew = await RefusedAsync(flowRunId, dispatchId, Done());

        Assert.Equal(
            $"Step 'worktree' of flow run {Ids.Short(flowRunId)} was resumed after dispatch {Ids.Short(dispatchId)}, so the resume dispatches it again under a new id; an outcome under dispatch {Ids.Short(dispatchId)} is not recorded.",
            beforeNew.Message);
        Assert.NotEqual(dispatchId, replacement);
        Assert.Contains($"holds dispatch {Ids.Short(replacement)}, not {Ids.Short(dispatchId)}", afterNew.Message, StringComparison.Ordinal);
        // the new dispatch takes its outcome
        await flowRuns.RecordOutcomeAsync(Principal.LocalOwner, Project, flowRunId, replacement, Done(), None);
    }

    [Fact]
    public async Task Running_is_no_outcome()
    {
        (Guid flowRunId, Guid dispatchId) = await DispatchedAsync();

        DomainException refused = await RefusedAsync(flowRunId, dispatchId, new SystemStepAnswer.Running());

        Assert.Contains("running is no outcome", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_visit_awaiting_its_dispatchs_outcome_is_not_dispatched_under_a_new_id()
    {
        (Guid flowRunId, Guid dispatchId) = await DispatchedAsync();

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => flowRuns.DispatchedAsync(Principal.LocalOwner, Project, flowRunId, "worktree", None));

        Assert.Contains($"holds dispatch {Ids.Short(dispatchId)}, which has no outcome and has not been resumed since", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Starts the shipped build flow on a task and records a dispatch of its worktree step, as the runner would before calling it.</summary>
    private async Task<(Guid FlowRunId, Guid DispatchId)> DispatchedAsync()
    {
        var parcels = new ParcelService(store, TimeProvider.System);
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Dispatched", null, None);
        await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: None);
        await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
        FlowRun flowRun = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
        FlowRun dispatched = await flowRuns.DispatchedAsync(Principal.LocalOwner, Project, flowRun.Id, "worktree", None);
        return (flowRun.Id, dispatched.Current?.DispatchId ?? throw new InvalidOperationException("No dispatch was recorded."));
    }

    private Task<DomainException> RefusedAsync(Guid flowRunId, Guid dispatchId, SystemStepAnswer outcome) =>
        Assert.ThrowsAsync<DomainException>(() => flowRuns.RecordOutcomeAsync(Principal.LocalOwner, Project, flowRunId, dispatchId, outcome, None));

    private static SystemStepAnswer.Done Done() =>
        new(JsonSerializer.SerializeToElement(new { verdict = "done" }));

    private async Task<IReadOnlyList<object>> EventsOfAsync(Guid flowRunId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(flowRunId, token: None);
        return [.. events.Select(e => e.Data)];
    }
}
