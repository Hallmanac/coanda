using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Triggers.Events;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Coanda.Tests.Daemon.Triggers.TriggerFiring;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// A published trigger on <c>attention</c>: each pause this node records of a flow run, other than one at a
/// wait-for-external-event step, creates a parcel derived from the paused flow run's parcel and flow run and starts
/// the trigger's flow there, within the trigger's hourly cap. An imported pause and an external event of the kind
/// fire nothing. Every pause here is seeded before the daemon starts, and the marker event recorded after them
/// says the loop has handled every one.
/// </summary>
public sealed class AttentionTriggerTests
{
    /// <summary>A flow whose two steps each wait for a person, so a test can pause it on either.</summary>
    private static readonly Flow Triage = new()
    {
        Name = "Triage",
        Accepts = ["task"],
        Outcome = "triaged",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            PersonStep("draft", "review"),
            PersonStep("review", Flow.End),
        ],
    };

    /// <summary>A flow whose first step waits for an external event.</summary>
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
                Arguments = new Dictionary<string, JsonElement> { [WaitForExternalEventSystemStep.EventArgument] = FlowValue.Of("deploy-finished") },
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    /// <summary>A trigger on attention creating a task titled by the paused parcel's title and starting <see cref="TriggerFiring.Hold"/> on it.</summary>
    private static TriggerDefinition OnAttention(string name, IReadOnlyDictionary<string, string> filters, string type = "task", int cap = 100) =>
        Creating(name, TriggerEvent.AttentionKind, filters, type: type) with { HourlyCap = cap };

    private static FlowStep PersonStep(string name, string next) => new()
    {
        Name = name,
        SystemStep = CoreSystemSteps.WaitForHuman,
        Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of("Resume to go on.") },
        Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = next }],
    };

    /// <summary>The principal and origin <paramref name="trigger"/> records for the pause recorded as <paramref name="pauseId"/>, written from their parts.</summary>
    private static string CreatedFor(string trigger, Guid pauseId) => $"trigger:project:alpha/{trigger}:pause:{pauseId}";

    /// <summary>Starts <see cref="Triage"/> on a new task titled <paramref name="title"/>; gives the parcel and the flow run.</summary>
    private static async Task<(Guid Parcel, Guid FlowRun)> TriagingAsync(Seeded seeded, string title)
    {
        Guid parcelId = await TaskAsync(seeded.Store, title);
        Guid flowRunId = (await seeded.FlowRuns.StartAsync(Principal.LocalOwner, Project, parcelId, Triage.Name, None)).Id;
        return (parcelId, flowRunId);
    }

    /// <summary>A person's pause of the flow run; gives the pause's event.</summary>
    private static async Task<IEvent> PersonPauseAsync(Seeded seeded, Guid flowRunId)
    {
        await seeded.FlowRuns.PauseAsync(Principal.LocalOwner, Project, flowRunId, "Needs a look.", "Resume it once looked at.", None);
        return await LatestPauseAsync(seeded.Store, flowRunId);
    }

    /// <summary>Dispatches the flow run's current step and records the paused answer it gives, as the runner does; gives the pause's event.</summary>
    private static async Task<IEvent> SystemPauseAsync(Seeded seeded, Guid flowRunId, string step)
    {
        Principal runner = FlowRunAdvancer.PrincipalFor(flowRunId);
        FlowRun dispatched = await seeded.FlowRuns.DispatchedAsync(runner, Project, flowRunId, step, None);
        Guid dispatchId = dispatched.Current?.DispatchId ?? throw new InvalidOperationException("The dispatch recorded no id.");
        await seeded.FlowRuns.RecordOutcomeAsync(
            runner, Project, flowRunId, dispatchId, new SystemStepAnswer.Paused($"Step '{step}' waits.", FlowRunLevers.PerformAgain(step)), None);
        return await LatestPauseAsync(seeded.Store, flowRunId);
    }

    /// <summary>Resumes the paused flow run, records its draft step done, and enters its review step.</summary>
    private static async Task OnToReviewAsync(Seeded seeded, Guid flowRunId)
    {
        await seeded.FlowRuns.ResumeAsync(Principal.LocalOwner, Project, flowRunId, null, null, None);
        using JsonDocument done = JsonDocument.Parse("""{"verdict":"done"}""");
        await seeded.FlowRuns.RecordResultAsync(Principal.LocalOwner, Project, flowRunId, "draft", CoreSystemSteps.WaitForHuman, done.RootElement.Clone(), None);
        await seeded.FlowRuns.StartStepAsync(Principal.LocalOwner, Project, flowRunId, "review", None);
    }

    private static async Task<IEvent> LatestPauseAsync(IDocumentStore store, Guid flowRunId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return (await session.Events.FetchStreamAsync(flowRunId, token: None)).Last(e => e.Data is FlowRunPaused);
    }

    /// <summary>The parcels the trigger named <paramref name="trigger"/> created, by the principal that created them.</summary>
    private static async Task<List<ParcelView>> ByTriggerAsync(DaemonClient client, string trigger) =>
        [.. (await ParcelsAsync(client)).Where(p => p.CreatedBy.StartsWith($"trigger:project:alpha/{trigger}:", StringComparison.Ordinal))];

    /// <summary>Asserts the one parcel <paramref name="trigger"/> created for <paramref name="pauseId"/>, its relation, and its flow run.</summary>
    private static async Task AssertCreatedAsync(RunningDaemon daemon, DaemonClient client, string trigger, Guid pauseId, Guid parcelId, Guid flowRunId, string title)
    {
        ParcelView view = Assert.Single(await ParcelsAsync(client), p => p.CreatedBy == CreatedFor(trigger, pauseId));
        Parcel created = await daemon.Services.GetRequiredService<ParcelService>().ShowAsync(Principal.LocalOwner, Project, view.Id, None)
            ?? throw new InvalidOperationException("No parcel.");
        Assert.Equal((title, CreatedFor(trigger, pauseId)), (created.Title, created.Origin));
        Assert.Equal([new ParcelRelation(RelationKinds.DerivedFrom, parcelId, flowRunId)], created.Relations);
        FlowRunView flowRun = await FlowRunDriver.BoundAsync(client, Project, view.Id);
        Assert.Equal((Hold.Name, CreatedFor(trigger, pauseId)), (flowRun.FlowName, flowRun.StartedBy));
    }

    [Fact]
    public async Task A_persons_pause_and_a_system_steps_paused_answer_each_fire_and_a_pause_at_a_wait_for_external_event_step_does_not()
    {
        using var temp = new TemporaryHome();
        (Guid Parcel, Guid FlowRun, Guid Pause) person = default;
        (Guid Parcel, Guid FlowRun, Guid Pause) system = default;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Triage);
            await FlowAsync(seeded.Store, Deploy);
            await TriggerAsync(seeded.Store, OnAttention("triaged", Fields(("flow", "triage"))));
            await TriggerAsync(seeded.Store, OnAttention("deploying", Fields(("flow", "deploy"))));

            (Guid parcel, Guid flowRun) = await TriagingAsync(seeded, "Paused by a person");
            person = (parcel, flowRun, (await PersonPauseAsync(seeded, flowRun)).Id);

            (parcel, flowRun) = await TriagingAsync(seeded, "Paused by its step");
            system = (parcel, flowRun, (await SystemPauseAsync(seeded, flowRun, "draft")).Id);

            Guid waiting = await TaskAsync(seeded.Store, "Waiting on a deploy");
            Guid deploy = (await seeded.FlowRuns.StartAsync(Principal.LocalOwner, Project, waiting, Deploy.Name, None)).Id;
            await SystemPauseAsync(seeded, deploy, "wait");
            await PersonPauseAsync(seeded, (await TriagingAsync(seeded, "Paused again later")).FlowRun);
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        await AssertCreatedAsync(daemon, client, "triaged", person.Pause, person.Parcel, person.FlowRun, "Paused by a person");
        await AssertCreatedAsync(daemon, client, "triaged", system.Pause, system.Parcel, system.FlowRun, "Paused by its step");
        Assert.Equal(3, (await ByTriggerAsync(client, "triaged")).Count);
        Assert.Empty(await ByTriggerAsync(client, "deploying"));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resumed_and_repaused_flow_run_fires_again_and_a_step_filter_reads_the_step_at_each_pause()
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid flowRunId = Guid.Empty;
        Guid first = Guid.Empty;
        Guid second = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Triage);
            await TriggerAsync(seeded.Store, OnAttention("any-pause", Fields(("flow", "Triage"))));
            await TriggerAsync(seeded.Store, OnAttention("at-draft", Fields(("step", "draft"))));

            // Both pauses are in the record before the loop handles the first, by which time the flow run is on review.
            (parcelId, flowRunId) = await TriagingAsync(seeded, "Paused twice");
            first = (await PersonPauseAsync(seeded, flowRunId)).Id;
            await OnToReviewAsync(seeded, flowRunId);
            second = (await PersonPauseAsync(seeded, flowRunId)).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        await AssertCreatedAsync(daemon, client, "any-pause", first, parcelId, flowRunId, "Paused twice");
        await AssertCreatedAsync(daemon, client, "any-pause", second, parcelId, flowRunId, "Paused twice");
        Assert.Equal(2, (await ByTriggerAsync(client, "any-pause")).Count);
        await AssertCreatedAsync(daemon, client, "at-draft", first, parcelId, flowRunId, "Paused twice");
        Assert.Single(await ByTriggerAsync(client, "at-draft"));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_pause_reads_as_exactly_four_fields_each_of_which_a_filter_matches_and_misses()
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid flowRunId = Guid.Empty;
        Guid pauseId = Guid.Empty;
        (string Name, string Field, string Value)[] matching =
        [
            ("m-flow", "flow", "triage"),
            ("m-step", "step", "draft"),
            ("m-type", "type", "task"),
            ("m-title", "title", "Needs triage"),
        ];
        (string Name, string Field, string Value)[] missing =
        [
            ("n-flow", "flow", "hold-on"),
            ("n-step", "step", "review"),
            ("n-type", "type", "bug"),
            ("n-title", "title", "Something else"),
            ("n-reason", "reason", "Needs a look."),
            ("n-lever", "lever", "Resume it once looked at."),
        ];
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Triage);

            // A cap of one keeps a trigger matching its own flow run's pause, or the marker's, to the one parcel for this pause.
            foreach ((string name, string field, string value) in matching.Concat(missing))
            {
                await TriggerAsync(seeded.Store, OnAttention(name, Fields((field, value)), cap: 1));
            }

            (parcelId, flowRunId) = await TriagingAsync(seeded, "Needs triage");
            pauseId = (await PersonPauseAsync(seeded, flowRunId)).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        foreach ((string name, string _, string _) in matching)
        {
            await AssertCreatedAsync(daemon, client, name, pauseId, parcelId, flowRunId, "Needs triage");
        }

        foreach ((string name, string _, string _) in missing)
        {
            Assert.Empty(await ByTriggerAsync(client, name));
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_imported_pause_and_an_external_event_of_the_kind_fire_no_trigger_and_a_stored_subscription_still_fires_on_the_event()
    {
        using var temp = new TemporaryHome();
        Guid importedParcel = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Triage);
            await TriggerAsync(seeded.Store, OnAttention("triaged", Fields(("flow", "triage"))));
            await StoredSubscriptions.RecordAsync(
                seeded.Store, Project, Principal.LocalOwner, new Trigger(TriggerEvent.AttentionKind, Fields(("title", "Recorded long ago"))), "task", "title", Hold.Name, None);

            // Another node's flow run, paused there, as the import brings it in.
            (importedParcel, Guid flowRunId) = await TriagingAsync(seeded, "Paused on a peer");
            await PersonPauseAsync(seeded, flowRunId);
            await ImportAsync(seeded.Store, flowRunId);

            // An external event of the kind recorded here before it was refused, and one imported from a peer's earlier build.
            Guid recordedHere = await AttentionEventAsync(seeded.Store, "Recorded long ago");
            Guid recordedThere = await AttentionEventAsync(seeded.Store, "Recorded on a peer");
            await ImportAsync(seeded.Store, recordedThere);
            Assert.NotEqual(recordedHere, recordedThere);
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        // Each external event carries the fields a pause of a Triage flow run reads as, so only the kind keeps the trigger off it.
        Assert.Empty(await ByTriggerAsync(client, "triaged"));
        ParcelView subscribed = Assert.Single(await CreatedAsync(client, importedParcel), p => p.CreatedBy == Principal.LocalOwner.Id);
        Assert.Equal("Recorded long ago", subscribed.Title);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_pause_handled_again_creates_no_second_parcel_and_starts_no_second_flow_run()
    {
        using var temp = new TemporaryHome();
        (Guid Id, long Sequence) pause = default;
        Guid parcelId = Guid.Empty;
        Guid flowRunId = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Triage);
            await TriggerAsync(seeded.Store, OnAttention("triaged", Fields(("flow", "triage"))));
            (parcelId, flowRunId) = await TriagingAsync(seeded, "Handled twice");
            IEvent paused = await PersonPauseAsync(seeded, flowRunId);
            pause = (paused.Id, paused.Sequence);
            await MarkAsync(seeded.Store, "Marker");
        });

        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            using var client = new DaemonClient(temp.Home);
            await ParcelTitledAsync(client, "Marker");
            await AssertCreatedAsync(first, client, "triaged", pause.Id, parcelId, flowRunId, "Handled twice");
            await first.StopAsync(None);
        }

        // A stop before the position moved past the pause: the next start handles it again.
        await using (IDocumentStore stopped = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await using IDocumentSession node = stopped.LightweightSession(Tenants.Node);
            node.Store(new TriggerPosition { Sequence = pause.Sequence - 1 });
            await node.SaveChangesAsync(None);
            await MarkAsync(stopped, "Marker again");
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var again = new DaemonClient(temp.Home);
        await ParcelTitledAsync(again, "Marker again");
        await AssertCreatedAsync(daemon, again, "triaged", pause.Id, parcelId, flowRunId, "Handled twice");
        ParcelView created = Assert.Single(await ByTriggerAsync(again, "triaged"));
        Assert.Single(await FlowRunDriver.OfParcelAsync(again, Project, created.Id));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_pause_handled_again_after_its_parcel_was_created_with_no_flow_run_starts_the_flow_on_that_parcel_once()
    {
        using var temp = new TemporaryHome();
        Guid pauseId = Guid.Empty;
        Guid parcelId = Guid.Empty;
        Guid flowRunId = Guid.Empty;
        Guid stranded = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Triage);
            await TriggerAsync(seeded.Store, OnAttention("triaged", Fields(("flow", "triage"))));
            (parcelId, flowRunId) = await TriagingAsync(seeded, "Stranded");
            pauseId = (await PersonPauseAsync(seeded, flowRunId)).Id;

            // The parcel a first handling created before a stop kept it from starting the flow.
            string origin = CreatedFor("triaged", pauseId);
            stranded = (await seeded.Parcels.AddAsync(
                new Principal(origin, Role.Member), Project, "task", "Stranded", null, origin, new DerivedFrom(parcelId, flowRunId), None)).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        await AssertCreatedAsync(daemon, client, "triaged", pauseId, parcelId, flowRunId, "Stranded");
        Assert.Equal(stranded, Assert.Single(await ByTriggerAsync(client, "triaged")).Id);
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, stranded));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_attention_trigger_at_its_hourly_cap_creates_no_second_parcel()
    {
        using var temp = new TemporaryHome();
        Guid firstPause = Guid.Empty;
        (Guid Parcel, Guid FlowRun) first = default;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Triage);
            await TriggerAsync(seeded.Store, OnAttention("triaged", Fields(("flow", "triage")), cap: 1));
            first = await TriagingAsync(seeded, "First pause");
            firstPause = (await PersonPauseAsync(seeded, first.FlowRun)).Id;
            await PersonPauseAsync(seeded, (await TriagingAsync(seeded, "Second pause")).FlowRun);
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        await AssertCreatedAsync(daemon, client, "triaged", firstPause, first.Parcel, first.FlowRun, "First pause");
        Assert.Single(await ByTriggerAsync(client, "triaged"));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_refused_attention_trigger_leaves_another_trigger_matching_the_same_pause_to_create_its_parcel()
    {
        using var temp = new TemporaryHome();
        Guid pauseId = Guid.Empty;
        (Guid Parcel, Guid FlowRun) paused = default;
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Triage);

            // Resolution orders triggers by name, so the refused one is handled before the other.
            await TriggerAsync(seeded.Store, OnAttention("a-broken", Fields(("flow", "triage")), type: "no-such-type"));
            await TriggerAsync(seeded.Store, OnAttention("b-working", Fields(("flow", "triage"))));
            paused = await TriagingAsync(seeded, "Paused once");
            pauseId = (await PersonPauseAsync(seeded, paused.FlowRun)).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        var logs = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { LoggerProvider = logs }, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        await AssertCreatedAsync(daemon, client, "b-working", pauseId, paused.Parcel, paused.FlowRun, "Paused once");
        Assert.Empty(await ByTriggerAsync(client, "a-broken"));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("trigger:project:alpha/a-broken", StringComparison.Ordinal)
            && entry.Message.Contains("parcel type 'no-such-type' does not resolve", StringComparison.Ordinal));
        await daemon.StopAsync(None);
    }

    /// <summary>Writes an external event of kind <c>attention</c> straight into the store, as one recorded before the kind was refused; gives its id.</summary>
    private static async Task<Guid> AttentionEventAsync(IDocumentStore store, string title)
    {
        var recorded = new ExternalEventRecorded(
            Ids.New(), Project, TriggerEvent.AttentionKind, Fields(("title", title), ("flow", "TRIAGE"), ("step", "draft"), ("type", "task")),
            Principal.LocalOwner.Id, DateTimeOffset.UtcNow, $"attention/{Ids.New()}", null);
        await using IDocumentSession session = store.LightweightSession(Project);
        session.Events.StartStream<ExternalEvent>(recorded.EventId, recorded);
        await session.SaveChangesAsync(None);
        return recorded.EventId;
    }
}
