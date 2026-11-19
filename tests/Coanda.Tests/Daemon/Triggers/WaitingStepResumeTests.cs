using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Store;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Transport;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using static Coanda.Tests.Daemon.Triggers.TriggerFiring;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// A flow run its own wait-for-external-event step paused resumes when this node records an external event of the
/// kind the step waits for that names the flow run's parcel, after the pause: the trigger loop resumes it as the
/// event's resumer, under the claim rule a start on the parcel obeys, and the step answers done. Every other event,
/// and every pause the step did not answer, leaves the flow run paused with nothing written to it.
/// </summary>
public sealed class WaitingStepResumeTests
{
    private const string Awaited = "deploy-finished";

    /// <summary>A flow whose first step waits for <see cref="Awaited"/> and whose done goes on to a person's step.</summary>
    private static readonly Flow Deploy = new()
    {
        Name = "deploy",
        Accepts = ["task"],
        Outcome = "deployed",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            new FlowStep
            {
                Name = "wait",
                SystemStep = CoreSystemSteps.WaitForExternalEvent,
                Arguments = new Dictionary<string, JsonElement> { [WaitForExternalEventSystemStep.EventArgument] = FlowValue.Of(Awaited) },
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = "approve" }],
            },
            new FlowStep
            {
                Name = "approve",
                SystemStep = CoreSystemSteps.WaitForHuman,
                Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of("Resume to approve.") },
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    [Theory]
    [InlineData(Standing.Held, false, true)]
    [InlineData(Standing.Held, true, true)]
    [InlineData(Standing.Unclaimed, false, true)]
    [InlineData(Standing.Unclaimed, true, false)]
    [InlineData(Standing.Lost, false, false)]
    [InlineData(Standing.Lost, true, false)]
    public async Task A_matching_event_resumes_the_waiting_step_where_the_claims_let_this_node(Standing standing, bool imported, bool resumes)
    {
        using var temp = new TemporaryHome();
        Guid flowRunId = Guid.Empty;
        Guid eventId = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Deploy);
            Guid parcelId = await TaskAsync(seeded.Store, "Waiting for a deploy");
            flowRunId = await PausedAtWaitAsync(seeded, parcelId);
            await seeded.StandAsync(parcelId, standing);
            eventId = (await seeded.RecordAsync(Awaited, Fields(), parcelId)).Id;
            if (imported)
            {
                await ImportAsync(seeded.Store, eventId);
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        await ParcelTitledAsync(client, "Marker");

        if (resumes)
        {
            await AtApproveAsync(client, flowRunId);
            FlowRunResumed resumed = Assert.Single(await ResumesAsync(store, flowRunId));
            Assert.Equal(TriggerLoop.ResumerFor(eventId).Id, resumed.ResumedBy);
            Assert.Equal($"external-event:{eventId}", resumed.ResumedBy);
            Assert.Contains(eventId.ToString(), resumed.Note, StringComparison.Ordinal);
            Assert.Null(resumed.Answer);
        }
        else
        {
            Assert.Empty(await ResumesAsync(store, flowRunId));
            Assert.Equal("wait", (await FlowRunDriver.ShowAsync(client, Project, flowRunId)).CurrentStep);
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_event_that_is_not_the_one_the_step_awaits_leaves_the_flow_run_paused_and_writes_nothing_to_it()
    {
        using var temp = new TemporaryHome();
        Dictionary<string, Guid> flowRuns = [];
        Dictionary<string, long> versions = [];
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Deploy);

            Guid otherKind = await TaskAsync(seeded.Store, "Another kind");
            flowRuns["another kind"] = await PausedAtWaitAsync(seeded, otherKind);
            await seeded.RecordAsync("build-finished", Fields(), otherKind);

            Guid noParcel = await TaskAsync(seeded.Store, "No parcel named");
            flowRuns["no parcel"] = await PausedAtWaitAsync(seeded, noParcel);
            await seeded.RecordAsync(Awaited, Fields());

            Guid named = await TaskAsync(seeded.Store, "Another parcel named");
            Guid bystander = await TaskAsync(seeded.Store, "The parcel the event names");
            flowRuns["another parcel"] = await PausedAtWaitAsync(seeded, named);
            await seeded.RecordAsync(Awaited, Fields(), bystander);

            // The event comes before the step's pause in this node's record.
            Guid early = await TaskAsync(seeded.Store, "Event before the pause");
            Guid earlyRun = (await seeded.FlowRuns.StartAsync(Principal.LocalOwner, Project, early, Deploy.Name, None)).Id;
            await seeded.RecordAsync(Awaited, Fields(), early);
            await PauseOwnAsync(seeded, earlyRun);
            flowRuns["before the pause"] = earlyRun;

            // A person paused the flow run while the step's dispatch held no outcome.
            Guid personal = await TaskAsync(seeded.Store, "A person's pause");
            Guid personalRun = (await seeded.FlowRuns.StartAsync(Principal.LocalOwner, Project, personal, Deploy.Name, None)).Id;
            await seeded.FlowRuns.DispatchedAsync(FlowRunAdvancer.PrincipalFor(personalRun), Project, personalRun, "wait", None);
            await seeded.FlowRuns.PauseAsync(Principal.LocalOwner, Project, personalRun, "Held for a look.", "Resume when ready.", None);
            await seeded.RecordAsync(Awaited, Fields(), personal);
            flowRuns["a person's pause"] = personalRun;

            // A cap paused the flow run on the step before it was dispatched.
            Guid capped = await TaskAsync(seeded.Store, "A cap's pause");
            Guid cappedRun = (await seeded.FlowRuns.StartAsync(Principal.LocalOwner, Project, capped, Deploy.Name, None)).Id;
            await seeded.FlowRuns.PauseAsync(FlowRunAdvancer.PrincipalFor(cappedRun), Project, cappedRun, "The budget is spent.", "Raise the budget.", None);
            await seeded.RecordAsync(Awaited, Fields(), capped);
            flowRuns["a cap's pause"] = cappedRun;

            foreach ((string why, Guid flowRun) in flowRuns)
            {
                versions[why] = await VersionAsync(seeded.Store, flowRun);
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        await ParcelTitledAsync(client, "Marker");

        foreach ((string why, Guid flowRun) in flowRuns)
        {
            FlowRunView shown = await FlowRunDriver.ShowAsync(client, Project, flowRun);
            Assert.True(shown is { Status: "paused", CurrentStep: "wait" }, $"{why}: {shown.Status} at {shown.CurrentStep}");
            Assert.True(versions[why] == await VersionAsync(store, flowRun), $"{why}: the flow run was written to");
        }

        await daemon.StopAsync(None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_event_held_back_while_this_nodes_claim_is_not_yet_held_resumes_the_step_once_it_is(bool imported)
    {
        using var temp = new TemporaryHome();
        Guid flowRunId = Guid.Empty;
        Guid parcelId = Guid.Empty;
        long through = 0;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Deploy);
            parcelId = await TaskAsync(seeded.Store, "Claimed, not yet held");
            flowRunId = await PausedAtWaitAsync(seeded, parcelId);
            through = await seeded.ClaimAsync(parcelId, fetched: false);
            Guid eventId = (await seeded.RecordAsync(Awaited, Fields(), parcelId)).Id;
            if (imported)
            {
                await ImportAsync(seeded.Store, eventId);
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        await ParcelTitledAsync(client, "Marker");
        Assert.Empty(await ResumesAsync(store, flowRunId));
        Assert.Equal("wait", (await FlowRunDriver.ShowAsync(client, Project, flowRunId)).CurrentStep);

        // A fetch after the push holds the parcel, and the event held back resumes the step.
        await LedgerProgress.AdvanceAsync(store, Project, LedgerProgress.FetchedId, through, None);
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                await using IQuerySession session = store.QuerySession(Project);
                return await session.LoadAsync<HeldBackEntries>(parcelId, None) is null;
            },
            "the held back event is cleared");
        await AtApproveAsync(client, flowRunId);

        // Handled once more after a later entry, the event resumes nothing again.
        await MarkAsync(store, "Marker again");
        await ParcelTitledAsync(client, "Marker again");
        Assert.Single(await ResumesAsync(store, flowRunId));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_event_handled_again_after_a_stop_before_its_position_was_written_resumes_nothing_again()
    {
        using var temp = new TemporaryHome();
        Guid flowRunId = Guid.Empty;
        Guid eventId = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Deploy);
            Guid parcelId = await TaskAsync(seeded.Store, "Waiting for a deploy");
            flowRunId = await PausedAtWaitAsync(seeded, parcelId);
            eventId = (await seeded.RecordAsync(Awaited, Fields(), parcelId)).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            using var client = new DaemonClient(temp.Home);
            await ParcelTitledAsync(client, "Marker");
            await AtApproveAsync(client, flowRunId);
            await first.StopAsync(None);
        }

        // The position goes back to just before the event, as a stop between handling it and writing the position leaves it.
        await using (IDocumentStore stopped = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            long sequence;
            await using (IQuerySession read = stopped.QuerySession(Project))
            {
                sequence = (await read.Events.FetchStreamAsync(eventId, token: None))[0].Sequence;
            }

            await using IDocumentSession node = stopped.LightweightSession(Tenants.Node);
            node.Store(new TriggerPosition { Sequence = sequence - 1 });
            await node.SaveChangesAsync(None);
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, None);
        using var again = new DaemonClient(temp.Home);
        IDocumentStore store = second.Services.GetRequiredService<IDocumentStore>();
        await MarkAsync(store, "Marker again");
        await ParcelTitledAsync(again, "Marker again");

        Assert.Single(await ResumesAsync(store, flowRunId));
        FlowRunView shown = await FlowRunDriver.ShowAsync(again, Project, flowRunId);
        Assert.Equal(("paused", "approve"), (shown.Status, shown.CurrentStep));
        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_persons_resume_of_the_waiting_step_still_makes_it_answer_done()
    {
        using var temp = new TemporaryHome();
        Guid flowRunId = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Deploy);
            flowRunId = await PausedAtWaitAsync(seeded, await TaskAsync(seeded.Store, "Resumed by a person"));
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "Deployed by hand.");

        await AtApproveAsync(client, flowRunId);
        FlowRunResumed resumed = Assert.Single(await ResumesAsync(daemon.Services.GetRequiredService<IDocumentStore>(), flowRunId));
        Assert.False(resumed.ResumedBy.StartsWith("external-event:", StringComparison.Ordinal), resumed.ResumedBy);
        Assert.Equal("Deployed by hand.", resumed.Note);
        await daemon.StopAsync(None);
    }

    /// <summary>Starts <see cref="Deploy"/> on the parcel and pauses it at its wait step as the step's own dispatch does.</summary>
    private static async Task<Guid> PausedAtWaitAsync(Seeded seeded, Guid parcelId)
    {
        Guid flowRunId = (await seeded.FlowRuns.StartAsync(Principal.LocalOwner, Project, parcelId, Deploy.Name, None)).Id;
        await PauseOwnAsync(seeded, flowRunId);
        return flowRunId;
    }

    /// <summary>Dispatches the flow run's wait step and records the pause it answers, as the runner does.</summary>
    private static async Task PauseOwnAsync(Seeded seeded, Guid flowRunId)
    {
        Principal runner = FlowRunAdvancer.PrincipalFor(flowRunId);
        FlowRun dispatched = await seeded.FlowRuns.DispatchedAsync(runner, Project, flowRunId, "wait", None);
        Guid dispatchId = dispatched.Current?.DispatchId ?? throw new InvalidOperationException("The dispatch recorded no id.");
        await seeded.FlowRuns.RecordOutcomeAsync(
            runner, Project, flowRunId, dispatchId,
            new SystemStepAnswer.Paused($"Step 'wait' waits for the external event '{Awaited}'.", FlowRunLevers.PerformAgain("wait")),
            None);
    }

    /// <summary>Waits for the flow run to go on from its wait step to the person's step its done names.</summary>
    private static async Task AtApproveAsync(DaemonClient client, Guid flowRunId) =>
        await FlowRunDriver.UntilAsync(
            async () => await FlowRunDriver.ShowAsync(client, Project, flowRunId) is { Status: "paused", CurrentStep: "approve" },
            "the flow run goes on to its approve step");

    private static async Task<List<FlowRunResumed>> ResumesAsync(IDocumentStore store, Guid flowRunId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> stream = await session.Events.FetchStreamAsync(flowRunId, token: None);
        return [.. stream.Select(e => e.Data).OfType<FlowRunResumed>()];
    }
}
