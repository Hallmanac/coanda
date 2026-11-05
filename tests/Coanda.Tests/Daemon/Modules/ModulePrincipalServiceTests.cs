using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// The calls a module makes that have no loopback endpoint yet, at the service: recording an external
/// event of its own prefixed kind, which no other principal records, listing subscriptions, and
/// recording the outcome of a dispatch it holds; the refusals of entries closed to it; and the owner's
/// switching modules on for a project.
/// </summary>
public sealed class ModulePrincipalServiceTests
{
    private const string On = ModuleNode.On;
    private const string Off = ModuleNode.Off;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Module = Principal.ForModule(ModuleNode.Module);
    private static readonly Dictionary<string, string> Fields = new() { ["number"] = "12" };

    [Fact]
    public async Task A_module_records_an_external_event_only_of_a_kind_prefixed_with_its_own_name_in_a_project_it_is_switched_on_for()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        TriggerService triggers = node.Services.GetRequiredService<TriggerService>();

        ExternalEvent recorded = await triggers.RecordEventAsync(Module, On, "test/pr-opened", Fields, None);
        Assert.Equal("test/pr-opened", recorded.Kind);
        Assert.Equal("module:test", recorded.RecordedBy);

        DomainException unprefixed = await Assert.ThrowsAsync<DomainException>(() => triggers.RecordEventAsync(Module, On, "pr-opened", Fields, None));
        DomainException another = await Assert.ThrowsAsync<DomainException>(() => triggers.RecordEventAsync(Module, On, "other/pr-opened", Fields, None));
        DomainException elsewhere = await Assert.ThrowsAsync<DomainException>(() => triggers.RecordEventAsync(Module, Off, "test/pr-opened", Fields, None));
        Assert.Equal("Module 'test' records external events of kinds it prefixes with its own name, 'test/<name>'; 'pr-opened' is not one.", unprefixed.Message);
        Assert.Equal("Module 'test' records external events of kinds it prefixes with its own name, 'test/<name>'; 'other/pr-opened' is not one.", another.Message);
        Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, Off), elsewhere.Message);

        // A person records a plain kind in any project, but a prefixed kind belongs to its module alone, the owner included.
        DomainException byOwner = await Assert.ThrowsAsync<DomainException>(() => triggers.RecordEventAsync(Principal.LocalOwner, On, "test/pr-opened", Fields, None));
        DomainException byMember = await Assert.ThrowsAsync<DomainException>(
            () => triggers.RecordEventAsync(new Principal("teammate", Role.Member), On, "test/pr-opened", Fields, None));
        Assert.Equal("Only module 'test' records external events of kinds prefixed 'test/'; 'owner' cannot record 'test/pr-opened'.", byOwner.Message);
        Assert.Equal("Only module 'test' records external events of kinds prefixed 'test/'; 'teammate' cannot record 'test/pr-opened'.", byMember.Message);
        Assert.Equal("pr-opened", (await triggers.RecordEventAsync(new Principal("teammate", Role.Member), Off, "pr-opened", Fields, None)).Kind);

        await using IQuerySession on = node.Services.GetRequiredService<IDocumentStore>().QuerySession(On);
        Assert.Equal(["test/pr-opened"], (await on.Query<ExternalEvent>().ToListAsync(None)).Select(e => e.Kind));
    }

    [Fact]
    public async Task A_module_lists_the_subscriptions_of_a_project_it_is_switched_on_for_only()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        TriggerService triggers = node.Services.GetRequiredService<TriggerService>();
        await triggers.SubscribeAsync(
            Principal.LocalOwner, On, new Trigger("test/pr-opened", new Dictionary<string, string>()), "task", "number",
            RunningSystemStep.FlowNamed().Name, None);

        Assert.Equal("test/pr-opened", Assert.Single(await triggers.ListSubscriptionsAsync(Module, On, None)).EventKind);
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => triggers.ListSubscriptionsAsync(Module, Off, None));
        Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, Off), refused.Message);
    }

    [Fact]
    public async Task A_module_records_the_outcome_of_a_dispatch_of_its_own_step_that_it_holds_and_the_flow_run_routes_on_it()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        (_, Guid flowRunId, Guid dispatchId) = await node.DispatchedAsync(On);
        FlowRunService flowRuns = node.Services.GetRequiredService<FlowRunService>();

        FlowRun recorded = await flowRuns.RecordOutcomeAsync(Module, On, flowRunId, dispatchId, RunningSystemStep.Done(), None);

        Assert.NotNull(recorded.Current?.DispatchOutcomeAt);
        using var owner = new DaemonClient(node.Home);
        Assert.Equal("dispatched", (await FlowRunDriver.UntilAsync(owner, On, flowRunId, "ended")).Outcome);
    }

    [Fact]
    public async Task A_module_pauses_a_flow_run_as_the_outcome_of_a_dispatch_it_holds()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        (_, Guid flowRunId, Guid dispatchId) = await node.DispatchedAsync(On);
        FlowRunService flowRuns = node.Services.GetRequiredService<FlowRunService>();

        FlowRun paused = await flowRuns.RecordOutcomeAsync(
            Module, On, flowRunId, dispatchId, new SystemStepAnswer.Paused("The module needs a person.", "Resume once answered."), None);

        Assert.Equal(FlowRunStatus.Paused, paused.Status);
        Assert.Equal("The module needs a person.", paused.PauseReason);
        Assert.NotNull(paused.Current?.DispatchOutcomeAt);
        Assert.Equal("module:test", Assert.Single((await EventsAsync(node, On, flowRunId)).OfType<FlowRunPaused>()).PausedBy);
    }

    [Fact]
    public async Task An_outcome_from_a_module_is_refused_under_a_dispatch_it_does_not_hold_and_in_a_project_it_is_not_switched_on_for()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        await node.SwitchOnAsync(Off, ModuleNode.Module);
        (_, Guid flowRunId, Guid dispatchId) = await node.DispatchedAsync(On);
        (_, Guid stoppedId, Guid stoppedDispatch) = await node.DispatchedAsync(On, "Stopped");
        (_, Guid offId, Guid offDispatch) = await node.DispatchedAsync(Off, "Switched off");
        await node.SwitchOnAsync(On, ModuleNode.Module, ModuleNode.Other);
        await node.SwitchOnAsync(Off);
        FlowRunService flowRuns = node.Services.GetRequiredService<FlowRunService>();
        await flowRuns.StopAsync(Principal.LocalOwner, On, stoppedId, "Not needed.", None);
        Guid stranger = Ids.New();

        Assert.Equal(
            $"Step 'work' of flow run {Ids.Short(flowRunId)} holds dispatch {Ids.Short(dispatchId)}, not {Ids.Short(stranger)}; an outcome under dispatch {Ids.Short(stranger)} is not recorded.",
            (await RefusedAsync(flowRuns, Module, On, flowRunId, stranger)).Message);
        Assert.Equal(
            $"Dispatch {Ids.Short(dispatchId)} of flow run {Ids.Short(flowRunId)} performs the system step '{RunningSystemStep.StepName}', which is not module 'other''s; a module writes only on a dispatch of one of its own steps.",
            (await RefusedAsync(flowRuns, Principal.ForModule(ModuleNode.Other), On, flowRunId, dispatchId)).Message);
        Assert.Equal(
            $"Flow run {Ids.Short(stoppedId)} has been stopped; only a running flow run can record a dispatch's outcome.",
            (await RefusedAsync(flowRuns, Module, On, stoppedId, stoppedDispatch)).Message);
        Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, Off), (await RefusedAsync(flowRuns, Module, Off, offId, offDispatch)).Message);

        // Once its outcome is recorded, the dispatch is no longer held. The outcome is recorded through a
        // service with no runner behind it, so nothing routes the flow run on before the module's attempt.
        var unrouted = new FlowRunService(node.Services.GetRequiredService<IDocumentStore>(), TimeProvider.System);
        await unrouted.RecordOutcomeAsync(Module, On, flowRunId, dispatchId, RunningSystemStep.Done(), None);
        Assert.Equal(
            $"Step 'work' of flow run {Ids.Short(flowRunId)} already holds the outcome of dispatch {Ids.Short(dispatchId)}; an outcome under dispatch {Ids.Short(dispatchId)} is not recorded.",
            (await RefusedAsync(flowRuns, Module, On, flowRunId, dispatchId)).Message);
        Assert.Equal(FlowRunStatus.Running, (await ShowAsync(node, Off, offId)).Status);
        Assert.Null((await ShowAsync(node, Off, offId)).Current?.DispatchOutcomeAt);
    }

    [Fact]
    public async Task Entries_with_no_endpoint_that_a_module_may_not_use_are_refused_at_the_service()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        (_, Guid flowRunId, _) = await node.DispatchedAsync(On);
        FlowRunService flowRuns = node.Services.GetRequiredService<FlowRunService>();
        DefinitionService definitions = node.Services.GetRequiredService<DefinitionService>();
        TriggerService triggers = node.Services.GetRequiredService<TriggerService>();
        ProjectService projects = node.Services.GetRequiredService<ProjectService>();
        Flow flow = RunningSystemStep.FlowNamed("from-a-module");
        JsonElement result = JsonSerializer.SerializeToElement(new { verdict = "done" });

        (string Entry, Func<Task> Call)[] calls =
        [
            ("flow-run.result", () => flowRuns.RecordResultAsync(Module, On, flowRunId, "work", RunningSystemStep.StepName, result, None)),
            ("flow-run.dispatch", () => flowRuns.DispatchedAsync(Module, On, flowRunId, "work", None)),
            ("flow-run.end", () => flowRuns.EndAsync(Module, On, flowRunId, "dispatched", None)),
            ("definition.draft", () => definitions.DraftAsync(
                Module, DefinitionKind.Flow, DefinitionScope.Project, On, flow.Name, Flow.CanonicalJson(flow), flow.Hash, null, None)),
            ("definition.publish", () => definitions.PublishAsync(Module, On, Ids.New(), None)),
            ("subscription.add", () => triggers.SubscribeAsync(
                Module, On, new Trigger("test/pr-opened", new Dictionary<string, string>()), "task", "number", flow.Name, None)),
            ("project.set-modules", () => projects.SetModulesAsync(Module, On, [ModuleNode.Module, ModuleNode.Other], None)),
        ];
        foreach ((string entry, Func<Task> call) in calls)
        {
            DomainException refused = await Assert.ThrowsAsync<DomainException>(call);
            Assert.Equal(ModuleNode.NotOpen(entry), refused.Message);
        }

        Assert.Equal(FlowRunStatus.Running, (await ShowAsync(node, On, flowRunId)).Status);
        Assert.Equal([ModuleNode.Module], (await projects.ShowAsync(Principal.LocalOwner, On, None))?.Modules);
    }

    [Fact]
    public async Task Only_the_owner_switches_modules_on_and_the_set_is_recorded_in_the_projects_own_tenant()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        ProjectService projects = node.Services.GetRequiredService<ProjectService>();

        Project project = await projects.SetModulesAsync(Principal.LocalOwner, Off, ["mail", ModuleNode.Module], None);
        Assert.Equal(["mail", ModuleNode.Module], project.Modules);
        Assert.Equal(["mail", ModuleNode.Module], (await projects.ShowAsync(Principal.LocalOwner, Off, None))?.Modules);

        Principal[] refusedPrincipals =
        [
            new("teammate", Role.Member),
            FlowRunAdvancer.ScanPrincipal,
            Principal.ForSession(new SessionBinding(Off, Ids.New(), Ids.New())),
            Module,
        ];
        foreach (Principal principal in refusedPrincipals)
        {
            DomainException refused = await Assert.ThrowsAsync<DomainException>(() => projects.SetModulesAsync(principal, Off, [], None));
            Assert.Equal($"'project.set-modules' is not open to the {principal.Role} role.", refused.Message);
        }

        DomainException unregistered = await Assert.ThrowsAsync<DomainException>(() => projects.SetModulesAsync(Principal.LocalOwner, "gamma", ["mail"], None));
        Assert.Equal(ProjectService.Unregistered("gamma"), unregistered.Message);

        IDocumentStore store = node.Services.GetRequiredService<IDocumentStore>();
        await using (IQuerySession own = store.QuerySession(Off))
        {
            IReadOnlyList<IEvent> events = await own.Events.FetchStreamAsync(project.Id, token: None);
            // the registration switched the default modules on; the owner's set replaced them
            Assert.Equal([["software"], ["mail", ModuleNode.Module]], events.Select(e => e.Data).OfType<ProjectModulesSet>().Select(s => s.Modules));
        }

        await using IQuerySession nodeTenant = store.QuerySession(Tenants.Node);
        Assert.Empty(await nodeTenant.Events.FetchStreamAsync(project.Id, token: None));
    }

    private static Task<DomainException> RefusedAsync(FlowRunService flowRuns, Principal principal, string project, Guid flowRunId, Guid dispatchId) =>
        Assert.ThrowsAsync<DomainException>(() => flowRuns.RecordOutcomeAsync(principal, project, flowRunId, dispatchId, RunningSystemStep.Done(), None));

    private static async Task<IReadOnlyList<object>> EventsAsync(ModuleNode node, string project, Guid streamId)
    {
        await using IQuerySession session = node.Services.GetRequiredService<IDocumentStore>().QuerySession(project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(streamId, token: None);
        return [.. events.Select(e => e.Data)];
    }

    private static async Task<FlowRun> ShowAsync(ModuleNode node, string project, Guid flowRunId) =>
        await node.Services.GetRequiredService<FlowRunService>().ShowAsync(Principal.LocalOwner, project, flowRunId, None)
            ?? throw new InvalidOperationException($"No flow run {flowRunId}.");
}
