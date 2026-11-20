using System.Globalization;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Triggers;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Harbor;

/// <summary>
/// The gate of phase 4 (plan section 6.3): a module with no software noun in it, <see cref="HarborModule"/>,
/// runs end to end on the scripted executor, hosted by a daemon composed as <c>coa daemon run</c> composes one,
/// with no change to Core or the daemon made for it. The test drives the run through the loopback API and the
/// module's own core client, and reads the daemon's record in process only to observe it, except for the one
/// relation no surface records yet.
/// </summary>
public sealed class HarborGateTests
{
    private const string Project = "quay";
    private const string ArrivalId = "arrival-7";
    private static readonly CancellationToken None = CancellationToken.None;

    private const string SurveyAnswer = """
        {"verdict":"done","summary":"Two crates on deck, both marked for the east shed.","parcels":[{"type":"crate","title":"Crate of rope"},{"type":"crate","title":"Crate of lamps"}]}
        """;

    private static ScriptEntry Answer(string json, int ordinal) => new(json, $"/transcripts/{ordinal}.jsonl", Scripts.Usage(ordinal));

    private static string Count(string summary) => $$"""{"verdict":"done","summary":"{{summary}}"}""";

    /// <summary>A visit's recording as a context source renders it (<see cref="ContextAssembler"/>).</summary>
    private static string Recorded(FlowRunStepView visit) =>
        $"recorded {visit.RecordedAt?.ToString("O", CultureInfo.InvariantCulture)}: {Fences.Inline((visit.Answer ?? visit.Result)?.GetRawText() ?? "missing")}";

    [Fact]
    public async Task A_module_the_engine_has_never_heard_of_runs_the_phase_four_gate_scenario()
    {
        using var temp = new TemporaryHome();
        using var harbor = new HarborModule();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["surveyor"] = [Answer(SurveyAnswer, 1)],
            ["clerk"] = [Answer(Count("Two crates, counted from the origin."), 2), Answer(Count("Two crates, counted from the survey."), 3)],
        });
        var options = new DaemonOptions { Executor = executor, ComposedModules = [harbor.Compose()], DefaultModules = [HarborModule.Name] };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, options, None);
        using var client = new DaemonClient(temp.Home);
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();

        DescribeAnswer described = await harbor.DescribeAsync(None);
        Assert.Equal([HarborModule.Arrival, HarborModule.Cleared], described.EventKinds.Select(k => k.Kind));
        Assert.All(described.EventKinds, k => Assert.StartsWith($"{HarborModule.Name}/", k.Kind, StringComparison.Ordinal));

        ProjectView project = await client.PostAsync<ProjectView>("/projects", new ProjectAddRequest(Project), None);
        Assert.Equal([HarborModule.Name], project.Modules);

        // An arrival the module records twice under one external id is one event, and the arrival trigger makes one vessel of it.
        var arrival = new RecordedEvent(Project, HarborModule.Arrival, ArrivalId, new Dictionary<string, string> { ["name"] = "Northern Star" });
        CoreExternalEvent recorded = await harbor.RecordAsync(arrival, None);
        Assert.Equal(recorded.Id, (await harbor.RecordAsync(arrival, None)).Id);
        string arrivalKey = TriggerKey.ForEvent($"trigger:module:{HarborModule.Name}/arrival", recorded.Id);
        ParcelView vessel = await CreatedByAsync(client, arrivalKey);
        Assert.Equal(("vessel", "Northern Star"), (vessel.Type, vessel.Title));

        // The dock flow the trigger started pauses at its wait for clearance, and the module's clearance event resumes it.
        FlowRunView dock = await FlowRunDriver.BoundAsync(client, Project, vessel.Id);
        Assert.Equal(("dock", arrivalKey), (dock.FlowName, dock.StartedBy));
        await AtAsync(client, dock.Id, "clearance");
        await harbor.RecordAsync(new RecordedEvent(Project, HarborModule.Cleared, "cleared-7", new Dictionary<string, string>(), vessel.Id), None);
        dock = await AtAsync(client, dock.Id, "berth");
        Assert.Equal(["clearance", "berth"], dock.Steps.Select(s => s.Step));
        Assert.Equal("done", Verdict(dock.Steps[0]));
        Assert.Null(dock.Steps[0].ResumeAnswer);

        // A person's answer at the berth is recorded and routed on: unload goes on to weigh, where turn-away would end the flow run.
        await FlowRunDriver.ResumeAsync(client, Project, dock.Id, "Room at the east shed", "unload");
        dock = await FlowRunDriver.UntilAsync(client, Project, dock.Id, "ended");
        Assert.Equal(["clearance", "berth", "weigh", "survey", "split"], dock.Steps.Select(s => s.Step));
        FlowRunStepView berth = dock.Steps[1];
        Assert.Equal("unload", Member(berth, "answer"));
        Assert.Equal("docked", dock.Outcome);

        // The module's own step was dispatched to it and its visit shows what the module answered.
        FlowRunStepView weigh = dock.Steps[2];
        Assert.Equal("done", Verdict(weigh));
        Assert.Equal(HarborModule.Tonnage, weigh.Result?.GetProperty("tonnage").GetInt32());
        DispatchRequest dispatched = Assert.Single(harbor.Dispatches, d => d.FlowRunId == dock.Id);
        Assert.Equal((HarborModule.Weigh, "east"), (dispatched.Step, dispatched.Arguments["scale"].GetString()));

        // The derive step made two crates, each derived from the vessel and the dock flow run.
        JsonElement split = dock.Steps[4].Result ?? throw new InvalidOperationException("The split step recorded no result.");
        Guid[] crates = [.. split.GetProperty(DeriveParcelsSystemStep.ParcelIdsMember).EnumerateArray().Select(id => Guid.Parse(id.GetString() ?? string.Empty))];
        Assert.Equal(2, crates.Length);
        foreach (Guid crate in crates)
        {
            Parcel derived = await ShowAsync(parcels, crate);
            Assert.Equal(("crate", "stacked"), (derived.TypeName, derived.Stage));
            Assert.Equal([new ParcelRelation(RelationKinds.DerivedFrom, vessel.Id, dock.Id)], derived.Relations);
        }

        // The docked trigger made a logbook of the dock flow run's end, and the recount on it reads that flow run's answers through its origin.
        string dockedKey = TriggerKey.ForFlowRunEnd($"trigger:module:{HarborModule.Name}/docked", dock.Id);
        ParcelView logbook = await CreatedByAsync(client, dockedKey);
        Assert.Equal(("logbook", "Northern Star"), (logbook.Type, logbook.Title));
        Assert.Equal([new ParcelRelation(RelationKinds.DerivedFrom, vessel.Id, dock.Id)], (await ShowAsync(parcels, logbook.Id)).Relations);
        FlowRunView recount = await FlowRunDriver.BoundAsync(client, Project, logbook.Id);
        Assert.Equal(("recount", dockedKey), (recount.FlowName, recount.StartedBy));
        await FlowRunDriver.UntilAsync(client, Project, recount.Id, "ended");
        ScriptedSpawn recountSpawn = executor.Spawns[1];
        Assert.Equal("clerk", recountSpawn.Agent);
        string[] originLines = recountSpawn.Context.Split('\n');
        Assert.Contains($"Source origin flow run: flow run {dock.Id} of flow dock", originLines);
        Assert.Contains($"- step survey: visit 1, {Recorded(dock.Steps[3])}", originLines);
        Assert.Contains("Two crates on deck", dock.Steps[3].Answer?.GetRawText() ?? string.Empty, StringComparison.Ordinal);

        // A later flow run of another flow on the vessel reads the dock flow run's survey through a named-flow source.
        FlowRunView tally = await FlowRunDriver.StartAsync(client, Project, vessel.Id, "tally");
        tally = await FlowRunDriver.UntilAsync(client, Project, tally.Id, "ended");
        ScriptedSpawn tallySpawn = executor.Spawns[2];
        Assert.Equal("clerk", tallySpawn.Agent);
        Assert.Contains($"Source flow dock step survey: flow run {dock.Id}, visit 1, {Recorded(dock.Steps[3])}", tallySpawn.Context.Split('\n'));

        // The second crate waits on the first: its move into unloading starts nothing while the first is open, and the unload flow once it closes.
        (Guid first, Guid second) = (crates[0], crates[1]);
        // No surface records a blocked-by relation in phase 4, so the parcel service records it in process, standing for the person who would.
        await parcels.RelateAsync(Principal.LocalOwner, Project, second, RelationKinds.BlockedBy, first, null, None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, second, "unloading"), None);
        await FlowRunDriver.UntilAsync(async () => await WaitingAsync(store, second) is not null, "the second crate's entry waits");
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, second));
        await client.PostAsync<ParcelView>("/parcels/close", new ParcelCloseRequest(Project, first, "unloaded", "Carried to the east shed"), None);
        FlowRunView unload = await FlowRunDriver.BoundAsync(client, Project, second);
        Assert.Equal("unload", unload.FlowName);
        await FlowRunDriver.UntilAsync(client, Project, unload.Id, "ended");

        // The module's client finds the tally flow run by the tally flow's content hash, and no flow run of another flow.
        string tallyHash = Flow.FromJson(await File.ReadAllTextAsync(Path.Combine(HarborModule.Path, "definitions", "flows", "tally.json"), None)).Hash;
        Assert.Equal(tallyHash, tally.FlowHash);
        CoreFlowRun found = Assert.Single(await harbor.FlowRunsAsync(Project, tallyHash, None));
        Assert.Equal((tally.Id, "tally"), (found.Id, found.FlowName));

        // With every later event handled, the arrival recorded twice is still one event and one vessel.
        await using (IQuerySession session = store.QuerySession(Project))
        {
            ExternalEvent held = Assert.Single(
                await session.Query<ExternalEvent>().ToListAsync(None), e => e.Kind == HarborModule.Arrival && e.ExternalId == ArrivalId);
            Assert.Equal(recorded.Id, held.Id);
        }

        ParcelView[] vessels = [.. (await ListAsync(client)).Where(p => p.CreatedBy.StartsWith($"trigger:module:{HarborModule.Name}/arrival:", StringComparison.Ordinal))];
        Assert.Equal([vessel.Id], vessels.Select(p => p.Id));
        Assert.Equal([dock.Id, tally.Id], (await FlowRunDriver.OfParcelAsync(client, Project, vessel.Id)).Select(f => f.Id));

        await daemon.StopAsync(None);
    }

    private static Task<List<ParcelView>> ListAsync(DaemonClient client) =>
        client.GetAsync<List<ParcelView>>($"/parcels?project={Project}", None);

    /// <summary>Waits for the one parcel <paramref name="createdBy"/> created.</summary>
    private static async Task<ParcelView> CreatedByAsync(DaemonClient client, string createdBy)
    {
        ParcelView? created = null;
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                created = (await ListAsync(client)).SingleOrDefault(p => p.CreatedBy == createdBy);
                return created is not null;
            },
            $"{createdBy} creates a parcel");
        return created ?? throw new InvalidOperationException("The wait ended with no parcel.");
    }

    /// <summary>Waits until the flow run is paused at <paramref name="step"/>.</summary>
    private static async Task<FlowRunView> AtAsync(DaemonClient client, Guid flowRunId, string step)
    {
        FlowRunView? flowRun = null;
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                flowRun = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
                return flowRun is { Status: "paused" } && flowRun.CurrentStep == step;
            },
            $"flow run {flowRunId} is paused at '{step}'");
        return flowRun ?? throw new InvalidOperationException("The wait ended with no flow run.");
    }

    private static async Task<Parcel> ShowAsync(ParcelService parcels, Guid parcelId) =>
        await parcels.ShowAsync(Principal.LocalOwner, Project, parcelId, None) ?? throw new InvalidOperationException($"No parcel {parcelId}.");

    private static async Task<WaitingEntries?> WaitingAsync(IDocumentStore store, Guid parcelId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return await session.LoadAsync<WaitingEntries>(parcelId, None);
    }

    private static string? Verdict(FlowRunStepView visit) => Member(visit, "verdict");

    private static string? Member(FlowRunStepView visit, string name) =>
        visit.Result is { } result && result.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;
}
