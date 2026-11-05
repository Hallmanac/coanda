using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Modules;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Coanda.Sdk.Client;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Modules.Software;
using Coanda.Tests.Modules.Software.GitHub;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// The daemon hosting the software module: its settings checked against the module's describe answer,
/// switched on for new projects and for projects an older node home holds, flows validated against
/// the steps the switched-on modules describe, a step no switched-on module provides pausing, a result
/// the step's result schema refuses pausing, and the module reading and writing through the core's API
/// as its own Module principal.
/// </summary>
public sealed class SoftwareModuleHostingTests
{
    private const string Project = SoftwareNode.Project;
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_software_modules_settings_are_checked_against_the_schema_its_describe_answer_declares()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync();
        ModuleSettingsService settings = node.Services.GetRequiredService<ModuleSettingsService>();

        Assert.Same(SoftwareDescribe.Settings, node.Services.GetRequiredService<IModuleSettingsSchemas>().Find(SoftwareSteps.Module));
        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => settings.SetAsync(Principal.LocalOwner, Project, SoftwareSteps.Module, "required-checks", Json("[]"), None));
        Assert.Equal("The value given for setting 'required-checks' of module 'software' does not meet the setting's schema: TooFewItems at #/.", refused.Message);
        Assert.Empty(await DocumentsAsync<ModuleSettings>(node, Project));

        ModuleSettingsShown shown = await settings.SetAsync(Principal.LocalOwner, Project, SoftwareSteps.Module, "build", Json("\"make all\""), None);

        Assert.Equal("\"make all\"", shown.Replicated.Single(s => s.Name == "build").Value?.GetRawText());
    }

    [Fact]
    public async Task A_new_project_has_the_software_module_switched_on_with_nothing_configured()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync();

        Project? project = await node.Projects.ShowAsync(Principal.LocalOwner, Project, None);

        Assert.Equal([SoftwareSteps.Module], project?.Modules);
        Assert.Empty(await DocumentsAsync<ModuleSettings>(node, Project));
        Assert.Empty(await DocumentsAsync<ModuleProfileSelection>(node, Tenants.Node));
        ModuleConfiguration state = await node.Services.GetRequiredService<ModuleSettingsService>().StateAsync(Project, SoftwareSteps.Module, None);
        Assert.Equal(ModuleState.Configured, state.State);
    }

    [Fact]
    public async Task A_project_an_older_node_home_holds_has_the_software_module_switched_on_at_start_and_one_whose_modules_were_named_is_left_alone()
    {
        using var temp = new TemporaryHome();
        Guid older;
        Guid named;
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.EnsureExists().EnsureDatabase().DatabasePath))
        {
            // What a node home written before modules were holds: the registration and nothing after it.
            older = await RegisterAsync(store, "older");
            named = await RegisterAsync(store, "named", new ProjectModulesSet(Guid.Empty, "named", [], "owner", DateTimeOffset.UnixEpoch));
        }

        await using Coanda.Daemon.Api.RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        ProjectService projects = daemon.Services.GetRequiredService<ProjectService>();

        Assert.Equal([SoftwareSteps.Module], (await projects.ShowAsync(Principal.LocalOwner, "older", None))?.Modules);
        Project? left = await projects.ShowAsync(Principal.LocalOwner, "named", None);
        Assert.NotNull(left);
        Assert.Empty(left.Modules);
        IDocumentStore started = daemon.Services.GetRequiredService<IDocumentStore>();
        ProjectModulesSet switchedOn = Assert.Single(await EventsAsync<ProjectModulesSet>(started, "older", older));
        Assert.Equal(ProjectService.UpgradePrincipal.Id, switchedOn.SetBy);
        Assert.Single(await EventsAsync<ProjectModulesSet>(started, "named", named));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_project_flow_is_validated_against_the_core_steps_and_the_steps_of_the_modules_switched_on_for_its_project()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync(projects: [Project, "plain"]);
        await node.Projects.SetModulesAsync(Principal.LocalOwner, "plain", [], None);
        Flow gating = OneStep("gating", SoftwareSteps.Gates, "done");

        await Publishing.FlowAsync(node.Daemon, Project, gating);
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => Publishing.FlowAsync(node.Daemon, "plain", gating));

        Assert.Equal("Step 'act' of flow 'gating' names the system step 'software/gates', which is not a known system step.", refused.Message);
        await Publishing.FlowAsync(node.Daemon, "plain", OneStep("emitting", CoreSystemSteps.EmitOutcome, "done"));
    }

    [Fact]
    public async Task A_flow_of_no_project_is_validated_against_the_core_steps_and_the_steps_of_every_module_the_node_hosts()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync();
        DefinitionService definitions = node.Services.GetRequiredService<DefinitionService>();

        await DraftOwnerAsync(definitions, OneStep("gating", SoftwareSteps.Gates, "done"));
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => DraftOwnerAsync(definitions, OneStep("publishing", "cms/publish", "done")));

        Assert.Equal("Step 'act' of flow 'publishing' names the system step 'cms/publish', which is not a known system step.", refused.Message);
    }

    [Fact]
    public async Task Drafting_a_flow_that_names_a_software_step_without_the_prefix_is_refused_naming_the_step()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync();

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => Publishing.FlowAsync(node.Daemon, Project, PreviousFlows.Build));

        Assert.Equal("Step 'worktree' of flow 'build' names the system step 'worktree', which is not a known system step.", refused.Message);
    }

    public static TheoryData<string, string, string> Unprovided => new()
    {
        { "the software module switched off", SoftwareSteps.Gates, "; it names module 'software', which is not switched on for the project." },
        { "a module no project switches on", "cms/publish", "; it names module 'cms', which is not switched on for the project." },
        { "an unprefixed step that is not one of the five", "deploy", "; it names no module." },
    };

    [Theory]
    [MemberData(nameof(Unprovided))]
    public async Task A_flow_run_reaching_a_step_no_switched_on_module_provides_pauses_to_perform_it_again_naming_the_step_and_the_module(
        string what, string systemStep, string named)
    {
        Assert.False(string.IsNullOrEmpty(what));
        await using SoftwareNode node = await SoftwareNode.StartAsync();
        Flow flow = OneStep("unprovided", systemStep, "done");
        await PublishLenientlyAsync(node, flow, systemStep);
        if (systemStep == SoftwareSteps.Gates)
        {
            await node.Projects.SetModulesAsync(Principal.LocalOwner, Project, [], None);
        }

        FlowRunView paused = await RunAsync(node, flow.Name, "paused");

        Assert.Equal($"Step 'act' names the system step '{systemStep}', which no module switched on for project '{Project}' provides{named}", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("act"), paused.Lever);
    }

    [Fact]
    public async Task A_done_result_its_steps_result_schema_refuses_pauses_with_the_lever_a_step_that_throws_gets()
    {
        var github = new FakeGitHub();
        github.On(r => r.IsOpen, _ => FakeGitHub.Json(HttpStatusCode.Created, new JsonObject
        {
            ["number"] = 0,
            ["html_url"] = FakeGitHub.Url,
        }));
        await using SoftwareNode node = await SoftwareNode.StartAsync(github: github);
        Flow flow = OneStep("opening", SoftwareSteps.OpenPr, "opened");
        await Publishing.FlowAsync(node.Daemon, Project, flow);

        FlowRunView paused = await RunAsync(node, flow.Name, "paused");

        Assert.StartsWith("Step 'act' could not perform the system step 'software/open-pr': module 'software' answered a result its step's result schema refuses: ", paused.PauseReason, StringComparison.Ordinal);
        Assert.Contains("NumberTooSmall", paused.PauseReason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("act"), paused.Lever);
        Assert.Null(paused.Steps.Single().Result);
    }

    [Fact]
    public async Task A_step_the_advancer_dispatches_reads_and_writes_as_the_software_modules_principal_and_its_write_is_refused_once_its_dispatch_has_its_outcome()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync();
        Flow flow = OneStep("opening", SoftwareSteps.OpenPr, "opened");
        await Publishing.FlowAsync(node.Daemon, Project, flow);
        ModuleCoreAccess access = node.Services.GetRequiredService<ModuleCoreAccess>();
        CoreApiOptions direct = access.Options();
        await using RecordingForwarder forwarder = await RecordingForwarder.StartAsync(node.Daemon.Info.BaseAddress, None);
        access.Connect(forwarder.Address, direct.Token);

        FlowRunView ended = await RunAsync(node, flow.Name, "ended");

        RecordingForwarder.Call read = Assert.Single(forwarder.Calls, c => c.Method == "GET" && c.PathAndQuery == $"/projects/{Project}");
        Principal? caller = await node.Services.GetRequiredService<ModuleTokens>().ResolveAsync(read.Token, None);
        Assert.Equal(Role.Module, caller?.Role);
        Assert.Equal(SoftwareSteps.Module, caller?.Module);
        Assert.All(forwarder.Calls, c => Assert.Equal(read.Token, c.Token));
        RecordingForwarder.Call write = Assert.Single(forwarder.Calls, c => c.Method == "POST" && c.PathAndQuery == "/parcels/set-field");
        Assert.Equal((int)HttpStatusCode.OK, write.Status);
        Assert.Equal(ended.Id.ToString(), JsonNode.Parse(write.Body)?["dispatch"]?["flowRunId"]?.GetValue<string>());

        // The same write, made again once the dispatch it names holds its outcome, is refused.
        using var http = new HttpClient();
        using var again = new HttpRequestMessage(HttpMethod.Post, new Uri(direct.Address, "/parcels/set-field"))
        {
            Content = new StringContent(write.Body, Encoding.UTF8, "application/json"),
        };
        again.Headers.Add(CoreApi.TokenHeader, write.Token);
        using HttpResponseMessage refused = await http.SendAsync(again, None);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    private static Flow OneStep(string name, string systemStep, string verdict) => new()
    {
        Name = name,
        Accepts = ["task"],
        Outcome = "acted",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            new FlowStep
            {
                Name = "act",
                SystemStep = systemStep,
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of(verdict), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    /// <summary>Publishes a project-scope flow through a definition service that knows <paramref name="systemStep"/>, as a node that provided it would have.</summary>
    private static async Task PublishLenientlyAsync(SoftwareNode node, Flow flow, string systemStep)
    {
        var definitions = new DefinitionService(
            node.Store, TimeProvider.System, systemSteps: new HashSet<string>([.. PreviousFlows.SystemSteps, systemStep], StringComparer.Ordinal));
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, flow.Name, Flow.CanonicalJson(flow), flow.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
    }

    private static Task<Definition> DraftOwnerAsync(DefinitionService definitions, Flow flow) =>
        definitions.DraftAsync(Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Owner, null, flow.Name, Flow.CanonicalJson(flow), flow.Hash, null, None);

    /// <summary>Starts <paramref name="flow"/> on a new task through the daemon's API and waits for its flow run to be <paramref name="status"/>.</summary>
    private static async Task<FlowRunView> RunAsync(SoftwareNode node, string flow, string status)
    {
        using var client = new DaemonClient(node.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Act on it", null), None);
        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, flow);
        return await FlowRunDriver.UntilAsync(client, Project, started.Id, status);
    }

    private static async Task<Guid> RegisterAsync(IDocumentStore store, string name, params ProjectModulesSet[] after)
    {
        ProjectRegistered registered = ProjectDecider.Register(
            name, RegisteredProjects.Remote, RegisteredProjects.Branch, null, "owner", DateTimeOffset.UnixEpoch);
        await using IDocumentSession session = store.LightweightSession(name);
        session.Events.StartStream<Project>(registered.ProjectId, [registered, .. after.Select(a => a with { ProjectId = registered.ProjectId })]);
        await session.SaveChangesAsync(None);
        return registered.ProjectId;
    }

    private static async Task<IReadOnlyList<T>> EventsAsync<T>(IDocumentStore store, string tenant, Guid streamId)
    {
        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(streamId, token: None);
        return [.. events.Select(e => e.Data).OfType<T>()];
    }

    private static async Task<IReadOnlyList<T>> DocumentsAsync<T>(SoftwareNode node, string tenant)
        where T : notnull
    {
        await using IQuerySession session = node.Store.QuerySession(tenant);
        return await session.Query<T>().ToListAsync(None);
    }

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
