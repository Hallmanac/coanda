using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Tests.Core;
using Fisher;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A flow version whose content names the software steps without the prefix is a current shape: a flow
/// run under it records each step by its <c>software/</c> name in the events as stored, and the flow
/// version and the flow run keep the content hash it was published with (<see cref="UnprefixedSoftwareSteps"/>).
/// </summary>
public sealed class UnprefixedFlowContentTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_flow_run_under_a_flow_version_naming_the_steps_unprefixed_records_each_step_by_its_current_name_and_keeps_the_flows_hash()
    {
        using var temp = new TemporaryHome();
        string database = temp.Home.EnsureExists().EnsureDatabase().DatabasePath;
        await using IDocumentStore store = await PublishedStoreAsync(database);
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

    private static JsonObject Parse(string data) => JsonNode.Parse(data)?.AsObject() ?? throw new InvalidOperationException("A stored event's data is an object.");

    private static async Task<IDocumentStore> PublishedStoreAsync(string database)
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
}
