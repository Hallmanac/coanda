using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Tests.Core;
using Coanda.Tests.Sdk;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The one place the daemon reads an earlier build's records in their current shape: its function turns
/// a stored event into its current shape with no store, every read of the store goes through it, a stored
/// document reads in its current shape too, and nothing this build appends carries an earlier shape.
/// </summary>
public sealed class EarlierRecordsTests
{
    private const string Project = "alpha";
    private const string Fixture = "tests/Coanda.Tests/Daemon/Fixtures/store-written-by-e86428f.sql";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public void The_function_gives_a_shipped_scope_draft_in_module_scope_and_leaves_its_content_and_hash_as_stored()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);
        StoredRow row = EarlierFlowRuns.Rows(temp.Home.DatabasePath).First(r => r.Type == "definition_drafted");

        StoredEvent upgraded = EarlierRecords.Upgrade(new StoredEvent(row.Type, row.Data));

        JsonObject before = Parse(row.Data);
        JsonObject after = Parse(upgraded.Data);
        Assert.Equal("definition_drafted", upgraded.Type);
        Assert.Equal("Shipped", before["scope"]?.GetValue<string>());
        Assert.Null(before["module"]);
        Assert.Equal(("Module", "software", "0.1.0"), (after["scope"]?.GetValue<string>(), after["module"]?.GetValue<string>(), after["moduleVersion"]?.GetValue<string>()));
        Assert.Equal(before["content"]?.GetValue<string>(), after["content"]?.GetValue<string>());
        Assert.Equal(before["contentHash"]?.GetValue<string>(), after["contentHash"]?.GetValue<string>());

        // Nothing else of the record moves.
        before["scope"] = "Module";
        before["module"] = "software";
        before["moduleVersion"] = "0.1.0";
        Assert.True(JsonNode.DeepEquals(before, after));
    }

    [Fact]
    public async Task The_function_names_each_flow_run_events_unprefixed_step_by_its_current_name_and_changes_nothing_else()
    {
        using var temp = new TemporaryHome();
        string database = temp.Home.EnsureExists().EnsureDatabase().DatabasePath;
        Guid flowRunId;
        await using (IDocumentStore store = await EarlierStoreAsync(database))
        {
            Parcel parcel = await TaskAsync(store);
            (flowRunId, Guid dispatchId) = await EarlierFlowRuns.DispatchedAsync(store, Project, parcel.Id, None);
            await AppendAsync(store, flowRunId,
                new SystemStepCompleted(flowRunId, "worktree", "worktree", JsonSerializer.SerializeToElement(new { verdict = "done" }), DateTimeOffset.UtcNow, dispatchId),
                new StepStarted(flowRunId, "gates", StepKind.SystemStep, "gates", DateTimeOffset.UtcNow));
        }

        IReadOnlyList<StoredRow> rows = EarlierFlowRuns.Rows(database, flowRunId);
        Assert.Equal(["flow_run_started", "system_step_dispatched", "system_step_completed", "step_started"], rows.Select(r => r.Type));
        Assert.Equal(["worktree", "worktree", "worktree", "gates"], rows.Select(EarlierFlowRuns.SystemStepOf));

        foreach (StoredRow row in rows)
        {
            StoredEvent upgraded = EarlierRecords.Upgrade(new StoredEvent(row.Type, row.Data));

            string? step = EarlierFlowRuns.SystemStepOf(row);
            Assert.Equal(row.Type, upgraded.Type);
            Assert.Equal($"software/{step}", EarlierFlowRuns.SystemStepOf(new StoredRow(row.Sequence, row.StreamId, row.Version, upgraded.Type, upgraded.Data, row.Tenant, row.Archived)));
            JsonObject before = Parse(row.Data);
            JsonObject after = Parse(upgraded.Data);
            string performer = row.Type switch
            {
                "flow_run_started" => "firstStepPerformer",
                "step_started" => "performer",
                _ => "systemStep",
            };
            before[performer] = $"software/{step}";
            Assert.True(JsonNode.DeepEquals(before, after), $"{row.Type} changed beyond its performer: {upgraded.Data}");
        }
    }

    [Fact]
    public void The_function_gives_a_current_event_back_as_it_was_given()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);
        StoredRow published = EarlierFlowRuns.Rows(temp.Home.DatabasePath).First(r => r.Type == "definition_published");
        StoredEvent[] current =
        [
            new(published.Type, published.Data),
            new("step_started", """{"flowRunId":"01a1aa2b-396f-76c0-b4f7-561db6135f30","step":"worktree","kind":1,"performer":"software/worktree","startedAt":"2026-11-05T06:00:19.192743+00:00"}"""),
            new("step_started", """{"flowRunId":"01a1aa2b-396f-76c0-b4f7-561db6135f30","step":"build","kind":0,"performer":"worktree","startedAt":"2026-11-05T06:00:19.192743+00:00"}"""),
            new("system_step_dispatched", """{"flowRunId":"01a1aa2b-396f-76c0-b4f7-561db6135f30","step":"deploy","systemStep":"deploy","dispatchId":"01a1aa2b-396f-76c0-b4f7-561db6135f31","dispatchedAt":"2026-11-05T06:00:19.192743+00:00"}"""),
            new("definition_drafted", """{"definitionId":"01a1aa2b-396f-76c0-b4f7-561db6135f30","kind":"Flow","name":"build","scope":"Module","version":1,"content":"{}","contentHash":"dcbfd639e88354c7","draftedBy":"coanda-seed","draftedAt":"2026-11-05T06:00:19.192743+00:00","module":"software","moduleVersion":"0.2.0"}"""),
        ];

        Assert.All(current, stored => Assert.Equal(stored, EarlierRecords.Upgrade(stored)));
    }

    [Fact]
    public async Task Every_read_of_an_earlier_flow_run_reads_its_visits_by_the_steps_current_name()
    {
        using var temp = new TemporaryHome();
        string database = temp.Home.EnsureExists().EnsureDatabase().DatabasePath;
        await using IDocumentStore store = await EarlierStoreAsync(database);
        Parcel parcel = await TaskAsync(store);
        (Guid flowRunId, Guid dispatchId) = await EarlierFlowRuns.DispatchedAsync(store, Project, parcel.Id, None);
        Assert.Equal(["worktree", "worktree"], EarlierFlowRuns.Rows(database, flowRunId).Select(EarlierFlowRuns.SystemStepOf));

        // A store opened afresh reads what the first wrote, as one opened before the daemon starts does.
        await using (IDocumentStore reading = StoreConfiguration.Open(database))
        await using (IQuerySession query = reading.QuerySession(Project))
        {
            IReadOnlyList<IEvent> fetched = await query.Events.FetchStreamAsync(flowRunId, token: None);
            IReadOnlyList<IEvent> queried = await query.Events.QueryEventsAsync(e => e.Sequence > 0, None);
            FlowRun? replayed = await query.Events.AggregateStreamAsync<FlowRun>(flowRunId, token: None);
            FlowRun? loaded = await query.LoadAsync<FlowRun>(flowRunId, None);
            IReadOnlyList<FlowRun> listed = await query.Query<FlowRun>().ToListAsync(None);

            Assert.Equal([SoftwareSteps.Worktree, SoftwareSteps.Worktree], fetched.Select(e => PerformerOf(e.Data)));
            Assert.Equal([SoftwareSteps.Worktree, SoftwareSteps.Worktree], queried.Where(e => e.StreamId == flowRunId).Select(e => PerformerOf(e.Data)));
            Assert.All(new[] { replayed, loaded, Assert.Single(listed) }, flowRun =>
                Assert.Equal((SoftwareSteps.Worktree, dispatchId), (flowRun?.Current?.Performer, flowRun?.Current?.DispatchId)));
        }

        await using IDocumentSession session = store.LightweightSession(Project);
        IEventStream<FlowRun> stream = await session.Events.FetchForWriting<FlowRun>(flowRunId, None);
        Assert.Equal(SoftwareSteps.Worktree, stream.Aggregate?.Current?.Performer);
    }

    [Fact]
    public async Task Every_read_of_a_definition_main_drafted_in_shipped_scope_reads_it_as_the_software_modules()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);
        IReadOnlyList<StoredRow> drafts = [.. EarlierFlowRuns.Rows(temp.Home.DatabasePath).Where(r => r.Type == "definition_drafted" && Parse(r.Data)["scope"]?.GetValue<string>() == "Shipped")];
        Assert.NotEmpty(drafts);

        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        await using IQuerySession query = store.QuerySession(Tenants.Node);
        IReadOnlyList<Definition> documents = await query.Query<Definition>().ToListAsync(None);
        foreach (StoredRow draft in drafts)
        {
            Guid id = Guid.Parse(draft.StreamId);
            DefinitionDrafted drafted = Assert.IsType<DefinitionDrafted>((await query.Events.FetchStreamAsync(id, token: None))[0].Data);
            Definition? replayed = await query.Events.AggregateStreamAsync<Definition>(id, token: None);
            Definition? loaded = await query.LoadAsync<Definition>(id, None);
            Definition listed = Assert.Single(documents, d => d.Id == id);

            Assert.Equal((DefinitionScope.Module, "software", "0.1.0"), (drafted.Scope, drafted.Module, drafted.ModuleVersion));
            Assert.All(new[] { replayed, loaded, listed }, definition =>
                Assert.Equal(
                    (DefinitionScope.Module, "software", "0.1.0", Parse(draft.Data)["contentHash"]?.GetValue<string>()),
                    (definition?.Scope, definition?.Module, definition?.ModuleVersion, definition?.ContentHash)));
        }
    }

    [Fact]
    public async Task A_flow_run_under_a_flow_version_naming_the_steps_unprefixed_records_each_step_by_its_current_name_and_keeps_the_flows_hash()
    {
        using var temp = new TemporaryHome();
        string database = temp.Home.EnsureExists().EnsureDatabase().DatabasePath;
        await using IDocumentStore store = await EarlierStoreAsync(database);
        Definition published = await new DefinitionResolver(store).ResolveAsync(Project, DefinitionKind.Flow, PreviousFlows.Build.Name, None)
            ?? throw new InvalidOperationException("The earlier build flow is not published.");
        Parcel parcel = await TaskAsync(store);
        var flowRuns = new FlowRunService(store, TimeProvider.System);
        JsonElement done = JsonSerializer.SerializeToElement(new { verdict = "done" });

        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel.Id, PreviousFlows.Build.Name, None);
        await flowRuns.DispatchedAsync(Principal.LocalOwner, Project, started.Id, "worktree", None);
        FlowRun recorded = await flowRuns.RecordResultAsync(Principal.LocalOwner, Project, started.Id, "worktree", "worktree", done, None);
        await flowRuns.PauseAsync(Principal.LocalOwner, Project, started.Id, "Held for the test.", "resume it", None);
        await flowRuns.ResumeAsync(Principal.LocalOwner, Project, started.Id, null, None);
        await flowRuns.StartStepAsync(Principal.LocalOwner, Project, started.Id, "gates", None);
        await flowRuns.DispatchedAsync(Principal.LocalOwner, Project, started.Id, "gates", None);
        await flowRuns.RecordResultAsync(Principal.LocalOwner, Project, started.Id, "gates", "gates", done, None);
        await flowRuns.StartStepAsync(Principal.LocalOwner, Project, started.Id, "open-pr", None);
        FlowRun dispatched = await flowRuns.DispatchedAsync(Principal.LocalOwner, Project, started.Id, "open-pr", None);
        await flowRuns.RecordOutcomeAsync(
            Principal.LocalOwner, Project, started.Id, dispatched.Current?.DispatchId ?? Guid.Empty, new SystemStepAnswer.Done(done), None);

        Assert.Equal(SoftwareSteps.Worktree, started.Current?.Performer);
        Assert.Equal(SoftwareSteps.Worktree, recorded.Steps[0].Performer);
        IReadOnlyList<StoredRow> rows = EarlierFlowRuns.Rows(database, started.Id);
        Assert.Equal(
            [
                ("flow_run_started", SoftwareSteps.Worktree), ("system_step_dispatched", SoftwareSteps.Worktree), ("system_step_completed", SoftwareSteps.Worktree),
                ("step_started", SoftwareSteps.Gates), ("system_step_dispatched", SoftwareSteps.Gates), ("system_step_completed", SoftwareSteps.Gates),
                ("step_started", SoftwareSteps.OpenPr), ("system_step_dispatched", SoftwareSteps.OpenPr), ("system_step_completed", SoftwareSteps.OpenPr),
            ],
            rows.Where(r => EarlierFlowRuns.PerformerEvents.Contains(r.Type)).Select(r => (r.Type, EarlierFlowRuns.SystemStepOf(r))));

        // The flow version keeps the content and hash it was stored with, and the flow run keeps that hash.
        Assert.Equal(PreviousFlows.Build.Hash, Parse(rows[0].Data)["flowHash"]?.GetValue<string>());
        Definition held = await new DefinitionResolver(store).ResolveAsync(Project, DefinitionKind.Flow, PreviousFlows.Build.Name, None)
            ?? throw new InvalidOperationException("The earlier build flow is gone.");
        Assert.Equal((published.Id, published.Content, PreviousFlows.Build.Hash), (held.Id, held.Content, held.ContentHash));
        Assert.Equal("worktree", Flow.FromJson(held.Content).First?.SystemStep);
    }

    private static string? PerformerOf(object data) => data switch
    {
        FlowRunStarted started => started.FirstStepPerformer,
        StepStarted started => started.Performer,
        SystemStepDispatched dispatched => dispatched.SystemStep,
        SystemStepCompleted completed => completed.SystemStep,
        _ => null,
    };

    private static JsonObject Parse(string data) => JsonNode.Parse(data)?.AsObject() ?? throw new InvalidOperationException("A stored event's data is an object.");

    private static async Task<IDocumentStore> EarlierStoreAsync(string database)
    {
        IDocumentStore store = await SeededStore.OpenAsync(database, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, None);
        await PreviousFlows.PublishAsync(store, Project, PreviousFlows.Build);
        return store;
    }

    private static async Task<Parcel> TaskAsync(IDocumentStore store)
    {
        var parcels = new ParcelService(store, TimeProvider.System);
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Earlier", null, None);
        await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, pullRequest: false, cancellationToken: None);
        return parcel;
    }

    private static async Task AppendAsync(IDocumentStore store, Guid flowRunId, params object[] events)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        session.Events.Append(flowRunId, events);
        await session.SaveChangesAsync(None);
    }

    private static void Restore(NodeHome home)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = home.EnsureExists().DatabasePath, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand restore = connection.CreateCommand();
        restore.CommandText = File.ReadAllText(RepositoryFiles.PathOf(Fixture));
        restore.ExecuteNonQuery();
    }
}
