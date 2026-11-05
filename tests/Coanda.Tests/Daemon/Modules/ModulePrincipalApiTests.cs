using System.Net;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A module calls the loopback API with a module token and acts as its module's principal: it reads in
/// the projects its module is switched on for, sets a field only under a dispatch of its own step that
/// it holds, and is refused every other call that has an endpoint.
/// </summary>
public sealed class ModulePrincipalApiTests
{
    private const string On = ModuleNode.On;
    private const string Off = ModuleNode.Off;
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_module_token_resolves_to_its_modules_principal_whatever_principal_header_it_carries_and_a_token_never_issued_is_refused()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        ModuleTokens tokens = node.Services.GetRequiredService<ModuleTokens>();
        string token = await node.TokenAsync(ModuleNode.Module);

        Assert.Equal(Principal.ForModule(ModuleNode.Module), await tokens.ResolveAsync(token, None));
        Assert.Null(await tokens.ResolveAsync("never-issued", None));
        Assert.Null(await tokens.ResolveAsync(string.Empty, None));

        // The principal header names the owner, but the call acts as the module: it is refused what only
        // the Module role is refused, and lists only the projects its module is switched on for.
        (HttpStatusCode status, string body) = await node.SendAsync(token, HttpMethod.Get, "/status", principalHeader: "owner");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains(ModuleNode.NotOpen("daemon.status"), body, StringComparison.Ordinal);
        using var module = new DaemonClient(node.Home, token);
        Assert.Equal([On], (await module.GetAsync<List<ProjectView>>("/projects", None)).Select(p => p.Name));
        (HttpStatusCode listed, string projects) = await node.SendAsync(token, HttpMethod.Get, "/projects", principalHeader: "owner");
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.DoesNotContain(Off, projects, StringComparison.Ordinal);

        // Health reads no token; a token the daemon never issued is refused before any principal is read.
        Assert.Equal(HttpStatusCode.OK, (await node.SendAsync(null, HttpMethod.Get, "/health")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await node.SendAsync("never-issued", HttpMethod.Get, "/projects", principalHeader: "owner")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await node.SendAsync(token + "x", HttpMethod.Get, $"/parcels?project={On}")).Status);

        // The daemon token still acts as the owner, who lists every project.
        using var owner = new DaemonClient(node.Home);
        Assert.Equal([On, Off], (await owner.GetAsync<List<ProjectView>>("/projects", None)).Select(p => p.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("software/build")]
    [InlineData("two words")]
    public async Task A_token_for_a_name_that_is_not_a_plain_name_is_refused_and_nothing_is_stored(string name)
    {
        await using ModuleNode node = await ModuleNode.StartAsync();

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => node.TokenAsync(name));

        Assert.Equal($"A module's name is a plain name (letters, digits, hyphens, and underscores); '{name}' is not.", refused.Message);
        await using IQuerySession session = node.Services.GetRequiredService<IDocumentStore>().QuerySession(Tenants.Node);
        // only the token the daemon issued the software module it hosts at its start is held
        Assert.Equal(["software"], (await session.Query<ModuleTokenRecord>().ToListAsync(None)).Select(r => r.Module));
    }

    [Fact]
    public async Task A_module_reads_in_a_project_it_is_switched_on_for_and_each_read_naming_any_other_project_is_refused()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        (Guid parcelOn, Guid flowRunOn, _) = await node.DispatchedAsync(On);
        (Guid parcelOff, Guid flowRunOff, _) = await node.DispatchedAsync(Off);
        string token = await node.TokenAsync(ModuleNode.Module);
        using var module = new DaemonClient(node.Home, token);

        Assert.Equal(On, (await module.GetAsync<ProjectView>($"/projects/{On}", None)).Name);
        Assert.Equal(parcelOn, Assert.Single(await module.GetAsync<List<ParcelView>>($"/parcels?project={On}", None)).Id);
        Assert.Equal(parcelOn, (await module.GetAsync<ParcelView>($"/parcels/{parcelOn}?project={On}", None)).Id);
        Assert.Equal(flowRunOn, Assert.Single(await module.GetAsync<List<FlowRunView>>($"/flow-runs?project={On}", None)).Id);
        Assert.Equal(flowRunOn, (await module.GetAsync<FlowRunView>($"/flow-runs/{flowRunOn}?project={On}", None)).Id);
        foreach (string path in new[] { $"/types?project={On}", $"/flows?project={On}", $"/agents?project={On}" })
        {
            Assert.Equal(HttpStatusCode.OK, (await node.SendAsync(token, HttpMethod.Get, path)).Status);
        }

        foreach (string project in new[] { Off, "gamma" })
        {
            string[] reads =
            [
                $"/projects/{project}",
                $"/parcels?project={project}",
                $"/parcels/{parcelOff}?project={project}",
                $"/flow-runs?project={project}",
                $"/flow-runs/{flowRunOff}?project={project}",
                $"/types?project={project}",
                $"/flows?project={project}",
                $"/agents?project={project}",
            ];
            foreach (string path in reads)
            {
                Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, project), await node.RefusedAsync(token, HttpMethod.Get, path));
            }
        }

        // Switching the module on for a project opens its reads; switching it off closes them again.
        await node.SwitchOnAsync(Off, ModuleNode.Module);
        Assert.Equal(parcelOff, (await module.GetAsync<ParcelView>($"/parcels/{parcelOff}?project={Off}", None)).Id);
        Assert.Equal([On, Off], (await module.GetAsync<List<ProjectView>>("/projects", None)).Select(p => p.Name));
        await node.SwitchOnAsync(Off);
        Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, Off), await node.RefusedAsync(token, HttpMethod.Get, $"/parcels?project={Off}"));
    }

    [Fact]
    public async Task Every_command_with_an_endpoint_but_set_field_is_refused_to_a_module_token_and_records_nothing()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        (Guid parcelId, Guid flowRunId, _) = await node.DispatchedAsync(On);
        Parcel idle = await node.ParcelAsync(On, "Idle");
        string token = await node.TokenAsync(ModuleNode.Module);
        int eventsBefore = (await EventsAsync(node, On, flowRunId)).Count;

        (HttpMethod Method, string Path, object? Body, string Entry)[] calls =
        [
            (HttpMethod.Post, "/parcels", new ParcelAddRequest(On, "task", "From a module", null), "parcel.add"),
            (HttpMethod.Post, "/parcels/move", new ParcelMoveRequest(On, idle.Id, "build"), "parcel.move"),
            (HttpMethod.Post, "/parcels/close", new ParcelCloseRequest(On, idle.Id, "dropped", "A module closes nothing."), "parcel.close"),
            (HttpMethod.Post, "/flow-runs", new FlowRunStartRequest(On, idle.Id, RunningSystemStep.FlowNamed().Name), "flow-run.start"),
            (HttpMethod.Post, $"/flow-runs/{flowRunId}/pause", new FlowRunPauseRequest(On, "A module pauses nothing."), "flow-run.pause"),
            (HttpMethod.Post, $"/flow-runs/{flowRunId}/resume", new FlowRunResumeRequest(On, null), "flow-run.resume"),
            (HttpMethod.Post, $"/flow-runs/{flowRunId}/stop", new FlowRunStopRequest(On, "A module stops nothing."), "flow-run.stop"),
            (HttpMethod.Post, "/projects", new ProjectAddRequest("gamma", RegisteredProjects.Remote, RegisteredProjects.Settings), "project.add"),
            (HttpMethod.Post, "/projects/profile", new ProjectSetProfileRequest(On, "work"), "project.set-profile"),
            (HttpMethod.Get, "/status", null, "daemon.status"),
            (HttpMethod.Post, "/stop", null, "daemon.stop"),
        ];
        foreach ((HttpMethod method, string path, object? body, string entry) in calls)
        {
            Assert.Equal(ModuleNode.NotOpen(entry), await node.RefusedAsync(token, method, path, body));
        }

        using var owner = new DaemonClient(node.Home);
        Assert.Equal(2, (await owner.GetAsync<List<ParcelView>>($"/parcels?project={On}", None)).Count);
        ParcelView stillIdle = await owner.GetAsync<ParcelView>($"/parcels/{idle.Id}?project={On}", None);
        Assert.Null(stillIdle.Resolution);
        Assert.Equal(idle.Stage, stillIdle.Stage);
        Assert.Null(stillIdle.LiveFlowRunId);
        Assert.Equal("running", (await FlowRunDriver.ShowAsync(owner, On, flowRunId)).Status);
        Assert.Equal(eventsBefore, (await EventsAsync(node, On, flowRunId)).Count);
        Assert.Equal(parcelId, (await owner.GetAsync<ParcelView>($"/parcels/{parcelId}?project={On}", None)).Id);
        Assert.Equal([On, Off], (await owner.GetAsync<List<ProjectView>>("/projects", None)).Select(p => p.Name));
        Assert.Equal(HttpStatusCode.OK, (await node.SendAsync(node.Daemon.Info.Token, HttpMethod.Get, "/status")).Status);
    }

    [Fact]
    public async Task A_module_sets_a_field_only_under_a_dispatch_of_its_own_step_that_it_holds_and_only_on_that_dispatchs_parcel()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        (Guid parcelId, Guid flowRunId, Guid dispatchId) = await node.DispatchedAsync(On);
        (Guid siblingParcel, Guid siblingFlowRun, Guid siblingDispatch) = await node.DispatchedAsync(On, "A sibling");
        string token = await node.TokenAsync(ModuleNode.Module);
        var held = new NamedDispatch(flowRunId, dispatchId);

        // Under the dispatch it holds, on that dispatch's parcel, the set passes and is recorded as the module's.
        using var module = new DaemonClient(node.Home, token);
        ParcelView set = await module.PostAsync<ParcelView>("/parcels/set-field", Objective(parcelId, "Set by the module.", held), None);
        Assert.Equivalent(FieldValue.OfText("Set by the module."), set.Fields.Single(f => f.Name == "objective").Value, strict: true);
        ParcelFieldSet recorded = Assert.Single((await EventsAsync(node, On, parcelId)).OfType<ParcelFieldSet>());
        Assert.Equal("module:test", recorded.SetBy);

        Guid stranger = Ids.New();
        Assert.Equal(
            "Module 'test' sets a parcel's field only by naming a dispatch of one of its own steps that it holds; this set-field named none.",
            await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Objective(parcelId, "No dispatch.", null)));
        Assert.Equal(
            $"Dispatch {Ids.Short(dispatchId)} is flow run {Ids.Short(flowRunId)}'s, which runs on parcel {Ids.Short(parcelId)}, not parcel {Ids.Short(siblingParcel)}; a write under a dispatch is made on its flow run's parcel only.",
            await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Objective(siblingParcel, "Another parcel.", held)));
        Assert.Equal(
            $"Step 'work' of flow run {Ids.Short(flowRunId)} holds dispatch {Ids.Short(dispatchId)}, not {Ids.Short(stranger)}; an outcome under dispatch {Ids.Short(stranger)} is not recorded.",
            await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Objective(parcelId, "No such dispatch.", new NamedDispatch(flowRunId, stranger))));
        Assert.Equal(
            $"Step 'work' of flow run {Ids.Short(flowRunId)} holds dispatch {Ids.Short(dispatchId)}, not {Ids.Short(siblingDispatch)}; an outcome under dispatch {Ids.Short(siblingDispatch)} is not recorded.",
            await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Objective(parcelId, "Not this flow run's.", new NamedDispatch(flowRunId, siblingDispatch))));
        Assert.Equal(
            $"No flow run {Ids.Short(stranger)} in project '{On}', so it holds no dispatch {Ids.Short(dispatchId)}.",
            await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Objective(parcelId, "No such flow run.", new NamedDispatch(stranger, dispatchId))));

        // Another module switched on for the project does not hold a dispatch of the test module's step.
        await node.SwitchOnAsync(On, ModuleNode.Module, ModuleNode.Other);
        string other = await node.TokenAsync(ModuleNode.Other);
        Assert.Equal(
            $"Dispatch {Ids.Short(dispatchId)} of flow run {Ids.Short(flowRunId)} performs the system step '{RunningSystemStep.StepName}', which is not module 'other''s; a module writes only on a dispatch of one of its own steps.",
            await node.RefusedAsync(other, HttpMethod.Post, "/parcels/set-field", Objective(parcelId, "Not my step.", held)));

        // A dispatch the outcome command would refuse is not held: one on a stopped flow run, and one
        // whose outcome was recorded.
        FlowRunService flowRuns = node.Services.GetRequiredService<FlowRunService>();
        await flowRuns.StopAsync(Principal.LocalOwner, On, siblingFlowRun, "Not needed.", None);
        Assert.Equal(
            $"Flow run {Ids.Short(siblingFlowRun)} has been stopped; only a running flow run can record a dispatch's outcome.",
            await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Objective(siblingParcel, "Stopped.", new NamedDispatch(siblingFlowRun, siblingDispatch))));
        await flowRuns.PauseAsync(Principal.LocalOwner, On, flowRunId, "Wait.", "Resume when ready.", None);
        Assert.Equal(
            $"Flow run {Ids.Short(flowRunId)} is paused; only a running flow run can record a dispatch's outcome.",
            await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Objective(parcelId, "Paused.", held)));
        await flowRuns.ResumeAsync(Principal.LocalOwner, On, flowRunId, null, None);
        await FlowRunDriver.UntilAsync(() => node.Step.Dispatches.Count(d => d.FlowRunId == flowRunId) == 2, "the resume dispatches the step again");
        Assert.Equal(
            $"Step 'work' of flow run {Ids.Short(flowRunId)} holds dispatch {Ids.Short(node.Step.Dispatches.Last(d => d.FlowRunId == flowRunId).DispatchId)}, not {Ids.Short(dispatchId)}; an outcome under dispatch {Ids.Short(dispatchId)} is not recorded.",
            await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Objective(parcelId, "Replaced.", held)));

        (Guid answeredParcel, Guid answeredFlowRun, Guid answeredDispatch) = await node.DispatchedAsync(On, "Answered");
        var unrouted = new FlowRunService(node.Services.GetRequiredService<IDocumentStore>(), TimeProvider.System);
        await unrouted.RecordOutcomeAsync(Principal.LocalOwner, On, answeredFlowRun, answeredDispatch, RunningSystemStep.Done(), None);
        Assert.Equal(
            $"Step 'work' of flow run {Ids.Short(answeredFlowRun)} already holds the outcome of dispatch {Ids.Short(answeredDispatch)}; an outcome under dispatch {Ids.Short(answeredDispatch)} is not recorded.",
            await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Objective(answeredParcel, "Answered.", new NamedDispatch(answeredFlowRun, answeredDispatch))));

        // The only field set is the one set under the held dispatch.
        Assert.Single((await EventsAsync(node, On, parcelId)).OfType<ParcelFieldSet>());
        Assert.Empty((await EventsAsync(node, On, answeredParcel)).OfType<ParcelFieldSet>());
        Assert.Empty((await EventsAsync(node, On, siblingParcel)).OfType<ParcelFieldSet>());
    }

    [Fact]
    public async Task A_set_field_under_a_dispatch_of_a_project_the_module_was_switched_off_for_is_refused()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        await node.SwitchOnAsync(Off, ModuleNode.Module);
        (Guid parcelId, Guid flowRunId, Guid dispatchId) = await node.DispatchedAsync(Off);
        string token = await node.TokenAsync(ModuleNode.Module);
        await node.SwitchOnAsync(Off);

        string refusal = await node.RefusedAsync(
            token, HttpMethod.Post, "/parcels/set-field", Objective(parcelId, "Switched off.", new NamedDispatch(flowRunId, dispatchId), Off));

        Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, Off), refusal);
        Assert.Empty((await EventsAsync(node, Off, parcelId)).OfType<ParcelFieldSet>());
    }

    [Fact]
    public async Task A_person_who_names_a_dispatch_on_a_set_field_is_held_to_it_and_one_who_names_none_sets_as_before()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        (Guid parcelId, Guid flowRunId, Guid dispatchId) = await node.DispatchedAsync(On);
        Parcel idle = await node.ParcelAsync(On, "Idle");
        ParcelService parcels = node.Services.GetRequiredService<ParcelService>();

        await parcels.SetFieldAsync(Principal.LocalOwner, On, idle.Id, "objective", FieldValue.OfText("As before."), None);
        await parcels.SetFieldAsync(Principal.LocalOwner, On, parcelId, "objective", FieldValue.OfText("Under it."), new NamedDispatch(flowRunId, dispatchId), None);
        DomainException elsewhere = await Assert.ThrowsAsync<DomainException>(() => parcels.SetFieldAsync(
            Principal.LocalOwner, On, idle.Id, "objective", FieldValue.OfText("Elsewhere."), new NamedDispatch(flowRunId, dispatchId), None));

        Assert.StartsWith($"Dispatch {Ids.Short(dispatchId)} is flow run {Ids.Short(flowRunId)}'s", elsewhere.Message, StringComparison.Ordinal);
        Assert.Equal(["As before."], (await EventsAsync(node, On, idle.Id)).OfType<ParcelFieldSet>().Select(e => e.Value.Text));
    }

    private static ParcelSetFieldRequest Objective(Guid parcelId, string text, NamedDispatch? dispatch, string project = On) =>
        new(project, parcelId, "objective", FieldValue.OfText(text), dispatch);

    private static async Task<IReadOnlyList<object>> EventsAsync(ModuleNode node, string project, Guid streamId)
    {
        await using IQuerySession session = node.Services.GetRequiredService<IDocumentStore>().QuerySession(project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(streamId, token: None);
        return [.. events.Select(e => e.Data)];
    }
}
