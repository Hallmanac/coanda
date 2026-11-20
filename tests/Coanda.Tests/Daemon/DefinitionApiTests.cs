using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Projects;
using Coanda.Tests.Daemon.FlowRuns;
using Microsoft.Extensions.DependencyInjection;
using ModuleNode = Coanda.Tests.Daemon.Modules.ModuleNode;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A person drafts, publishes, and retires a parcel type, a flow, or an agent through the loopback API,
/// and lists every definition that can govern a project, of every status, to find the one to act on.
/// </summary>
public sealed class DefinitionApiTests
{
    private const string Project = "alpha";
    private const string Other = "beta";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_draft_of_each_kind_answers_its_row_and_records_the_content_and_hash_the_import_would_however_the_file_is_laid_out()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);

        (string Word, DefinitionKind Kind, string File)[] files =
        [
            ("parcel-type", DefinitionKind.ParcelType, ParcelType.CanonicalJson(SoftwareTypes.UnownedTask)),
            ("flow", DefinitionKind.Flow, SoftwareDefinitions.Read("flows", "build")),
            ("agent", DefinitionKind.Agent, SoftwareDefinitions.Read("agents", "fix")),
        ];

        foreach ((string word, DefinitionKind kind, string file) in files)
        {
            ModuleDefinition imported = ModuleDefinition.Read(kind, file);
            string relaid = Relaid(file);
            Assert.NotEqual(file, relaid);

            DefinitionView drafted = await DraftAsync(client, new DefinitionDraftRequest(Project, word, relaid));

            Assert.Equal((word, imported.Name, "project", Project, 1, "draft", imported.ContentHash), (drafted.Kind, drafted.Name, drafted.Scope, drafted.Project, drafted.Version, drafted.Status, drafted.Hash));
            Assert.Null(drafted.ForkBase);
            DefinitionDetailView shown = await client.GetAsync<DefinitionDetailView>($"/definitions/{drafted.Id}?project={Project}", None);
            Assert.Equal(drafted, shown.Definition);
            Assert.Equal(imported.Content, shown.Content);
        }

        // the same file laid out a second way is the next version of the same content and hash
        DefinitionView again = await DraftAsync(client, new DefinitionDraftRequest(Project, "flow", SoftwareDefinitions.Read("flows", "build")));
        Assert.Equal((2, SoftwareFlows.Build.Hash), (again.Version, again.Hash));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_draft_that_breaks_a_rule_is_refused_with_the_rules_reason_and_records_nothing()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        string flow = SoftwareDefinitions.Read("flows", "build");
        IReadOnlyList<DefinitionView> before = await ListAsync(client, Project);

        DomainException notAnAgent = await RefusedAsync(client, new DefinitionDraftRequest(Project, "agent", flow));
        Assert.Equal(Assert.Throws<DomainException>(() => ModuleDefinition.Read(DefinitionKind.Agent, flow)).Message, notAnAgent.Message);

        DomainException unknownStep = await RefusedAsync(client, new DefinitionDraftRequest(Project, "flow", WithSystemStep(flow, "no-such-step")));
        Assert.Contains("'no-such-step'", unknownStep.Message, StringComparison.Ordinal);

        DomainException module = await RefusedAsync(client, new DefinitionDraftRequest(Project, "flow", flow, "module"));
        Assert.Equal(
            "A definition cannot be drafted into module scope; module-scope definitions enter the store only when a module's definitions are imported.",
            module.Message);

        DomainException unknownKind = await RefusedAsync(client, new DefinitionDraftRequest(Project, "workflow", flow));
        Assert.Equal("'workflow' is not a kind of definition; a kind is one of parcel-type, flow, agent, trigger.", unknownKind.Message);

        Assert.Equal(before, await ListAsync(client, Project));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_project_that_is_not_registered_has_nothing_drafted_listed_published_or_retired_in_it()
    {
        const string Unregistered = "gamma";
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        string flow = SoftwareDefinitions.Read("flows", "build");
        DefinitionView drafted = await DraftAsync(client, new DefinitionDraftRequest(Project, "flow", flow));
        string refusal = ProjectService.Unregistered(Unregistered);

        Assert.Equal(refusal, (await RefusedAsync(client, new DefinitionDraftRequest(Unregistered, "flow", flow))).Message);
        Assert.Equal(refusal, (await Assert.ThrowsAsync<DomainException>(() => ListAsync(client, Unregistered))).Message);
        foreach (string verb in (string[])["publish", "retire"])
        {
            DomainException refused = await Assert.ThrowsAsync<DomainException>(
                () => client.PostAsync<DefinitionView>($"/definitions/{verb}", new DefinitionIdRequest(Unregistered, drafted.Id), None));
            Assert.Equal(refusal, refused.Message);
        }

        // an owner-scope draft reads no project, so it does not need one registered
        DefinitionView owner = await DraftAsync(client, new DefinitionDraftRequest(Unregistered, "agent", SoftwareDefinitions.Read("agents", "fix"), "owner"));
        Assert.Equal(("owner", (string?)null), (owner.Scope, owner.Project));
        Assert.Equal("draft", Assert.Single(await ListAsync(client, Project), d => d.Id == drafted.Id).Status);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_request_that_leaves_out_the_content_or_the_project_is_refused_rather_than_failing()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        DefinitionView drafted = await DraftAsync(client, new DefinitionDraftRequest(Project, "flow", SoftwareDefinitions.Read("flows", "build")));

        DomainException noContent = await RefusedAsync(client, new DefinitionDraftRequest(Project, "flow", null));
        Assert.Equal("A draft needs the definition's content; none was given.", noContent.Message);
        foreach (string verb in (string[])["publish", "retire"])
        {
            DomainException noProject = await Assert.ThrowsAsync<DomainException>(
                () => client.PostAsync<DefinitionView>($"/definitions/{verb}", new DefinitionIdRequest(null, drafted.Id), None));
            Assert.Equal("A project name is required; none was given.", noProject.Message);
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_fork_carries_the_highest_module_version_of_its_kind_and_name_and_one_naming_none_or_a_kept_owned_field_without_it_is_refused()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);

        // a second module-scope version of the fix agent, which a fork then takes over the first
        AgentDefinition second = SoftwareAgents.Fix with { Persona = SoftwareAgents.Fix.Persona with { Role = "A second fixer." } };
        await daemon.Services.GetRequiredService<DefinitionService>().ImportAsync(
            ModuleArtifact.Of(SoftwareDefinitions.Manifest.Name, "99.0.0", [ModuleDefinition.FromAgent(second)]), None);
        AgentDefinition custom = SoftwareAgents.Fix with { Persona = SoftwareAgents.Fix.Persona with { Role = "A house-style fixer." } };

        DefinitionView forked = await DraftAsync(client, new DefinitionDraftRequest(Project, "agent", AgentDefinition.CanonicalJson(custom), Fork: true));

        Assert.Equal(new ForkBase("fix", SoftwareDefinitions.Manifest.Name, "99.0.0", second.Hash), forked.ForkBase);
        Assert.Equal(custom.Hash, forked.Hash);

        IReadOnlyList<DefinitionView> before = await ListAsync(client, Project);
        AgentDefinition nobody = custom with { Name = "nobody" };
        DomainException noBase = await RefusedAsync(client, new DefinitionDraftRequest(Project, "agent", AgentDefinition.CanonicalJson(nobody), Fork: true));
        Assert.Equal(
            "No module-scope agent named 'nobody' is held on this node, so a draft of it cannot fork one; a fork names the module-scope definition of the draft's own kind and name.",
            noBase.Message);

        // the module's task type keeps fields the module owns: drafted without the fork it is refused
        string task = SoftwareDefinitions.Read("types", "task");
        Assert.Contains(SoftwareTypes.Task.Fields, f => f.Owner is not null);
        await RefusedAsync(client, new DefinitionDraftRequest(Project, "parcel-type", task));
        Assert.Equal(before, await ListAsync(client, Project));

        DefinitionView type = await DraftAsync(client, new DefinitionDraftRequest(Project, "parcel-type", task, Fork: true));

        DefinitionView moduleTask = Assert.Single(before, d => d is { Kind: "parcel-type", Name: "task", Scope: "module" });
        Assert.Equal(new ForkBase("task", moduleTask.Module, moduleTask.ModuleVersion, moduleTask.Hash), type.ForkBase);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_published_project_flow_governs_in_place_of_the_modules_until_it_is_retired_and_each_transition_out_of_order_is_refused()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow custom = SoftwareFlows.Build with { Budget = SoftwareFlows.Build.Budget + 1m };

        DefinitionView drafted = await DraftAsync(client, new DefinitionDraftRequest(Project, "flow", Flow.CanonicalJson(custom)));
        Assert.Equal(SoftwareFlows.Build.Hash, await FlowHashAsync(client, "build"));

        DefinitionView published = await client.PostAsync<DefinitionView>("/definitions/publish", new DefinitionIdRequest(Project, drafted.Id), None);

        Assert.Equal("published", published.Status);
        Assert.Equal(custom.Hash, await FlowHashAsync(client, "build"));

        // publishing anything but a draft, retiring anything but a published definition, and retiring a
        // module-scope definition are each refused, and record nothing
        DefinitionView draft = await DraftAsync(client, new DefinitionDraftRequest(Project, "flow", Flow.CanonicalJson(custom with { Budget = custom.Budget + 1m })));
        IReadOnlyList<DefinitionView> before = await ListAsync(client, Project);
        DefinitionView moduleBuild = Assert.Single(before, d => d is { Kind: "flow", Name: "build", Scope: "module" });

        DomainException republish = await Assert.ThrowsAsync<DomainException>(
            () => client.PostAsync<DefinitionView>("/definitions/publish", new DefinitionIdRequest(Project, published.Id), None));
        Assert.Equal("Only a draft can be published; 'build' version 1 is published.", republish.Message);
        DomainException retireDraft = await Assert.ThrowsAsync<DomainException>(
            () => client.PostAsync<DefinitionView>("/definitions/retire", new DefinitionIdRequest(Project, draft.Id), None));
        Assert.Equal("Only a published definition can be retired; 'build' version 2 is a draft.", retireDraft.Message);
        DomainException retireModule = await Assert.ThrowsAsync<DomainException>(
            () => client.PostAsync<DefinitionView>("/definitions/retire", new DefinitionIdRequest(Project, moduleBuild.Id), None));
        Assert.StartsWith("A module-scope definition cannot be retired; 'build' version 1 came with module 'software'", retireModule.Message, StringComparison.Ordinal);
        DomainException unknown = await Assert.ThrowsAsync<DomainException>(
            () => client.PostAsync<DefinitionView>("/definitions/publish", new DefinitionIdRequest(Project, Ids.New()), None));
        Assert.StartsWith("No definition ", unknown.Message, StringComparison.Ordinal);
        Assert.Equal(before, await ListAsync(client, Project));

        DefinitionView retired = await client.PostAsync<DefinitionView>("/definitions/retire", new DefinitionIdRequest(Project, published.Id), None);

        Assert.Equal("retired", retired.Status);
        Assert.Equal(SoftwareFlows.Build.Hash, await FlowHashAsync(client, "build"));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_list_shows_every_definition_that_can_govern_the_project_of_every_status_and_scope_and_no_other_projects()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await RegisteredProjects.AddAsync(daemon, Other, None);
        using var client = new DaemonClient(temp.Home);
        Flow custom = SoftwareFlows.Build with { Budget = SoftwareFlows.Build.Budget + 1m };
        DefinitionView retired = await DraftAsync(client, new DefinitionDraftRequest(Project, "flow", Flow.CanonicalJson(custom)));
        await client.PostAsync<DefinitionView>("/definitions/publish", new DefinitionIdRequest(Project, retired.Id), None);
        await client.PostAsync<DefinitionView>("/definitions/retire", new DefinitionIdRequest(Project, retired.Id), None);
        DefinitionView draft = await DraftAsync(client, new DefinitionDraftRequest(Project, "flow", Flow.CanonicalJson(custom with { Budget = 99m })));
        AgentDefinition ownerAgent = SoftwareAgents.Fix with { Name = "owner-fixer" };
        DefinitionView owner = await DraftAsync(client, new DefinitionDraftRequest(Project, "agent", AgentDefinition.CanonicalJson(ownerAgent), "owner"));
        DefinitionView elsewhere = await DraftAsync(client, new DefinitionDraftRequest(Other, "flow", Flow.CanonicalJson(custom)));

        IReadOnlyList<DefinitionView> listed = await ListAsync(client, Project);

        Assert.Contains(listed, d => d.Id == retired.Id && d.Status == "retired" && d.Scope == "project" && d.Project == Project);
        Assert.Contains(listed, d => d.Id == draft.Id && d.Status == "draft" && d.Version == 2);
        Assert.Contains(listed, d => d.Id == owner.Id && d is { Scope: "owner", Project: null, Module: null, Status: "draft" });
        Assert.DoesNotContain(listed, d => d.Id == elsewhere.Id);
        Assert.Equal(
            SoftwareDefinitions.All.Select(d => (DefinitionWords.Of(d.Kind), d.Name, d.ContentHash)).Order(),
            listed.Where(d => d.Scope == "module").Select(d => (d.Kind, d.Name, d.Hash)).Order());
        Assert.All(listed.Where(d => d.Scope == "module"), d => Assert.Equal(
            (SoftwareDefinitions.Manifest.Name, SoftwareDefinitions.Manifest.Version, "published"), (d.Module, d.ModuleVersion, d.Status)));

        DefinitionDetailView shown = await client.GetAsync<DefinitionDetailView>($"/definitions/{owner.Id}?project={Project}", None);
        Assert.Equal(AgentDefinition.CanonicalJson(ownerAgent), shown.Content);
        DomainException notHere = await Assert.ThrowsAsync<DomainException>(
            () => client.GetAsync<DefinitionDetailView>($"/definitions/{elsewhere.Id}?project={Project}", None));
        Assert.Equal($"No definition {Ids.Short(elsewhere.Id)} can govern project '{Project}'.", notHere.Message);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_list_keeps_the_versions_of_one_name_together_whatever_the_case_each_was_drafted_in()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        foreach (string name in (string[])["Mixed", "Zeta", "apply", "mixed"])
        {
            await DraftAsync(client, new DefinitionDraftRequest(Project, "agent", AgentDefinition.CanonicalJson(SoftwareAgents.Fix with { Name = name })));
        }

        IReadOnlyList<DefinitionView> listed = await ListAsync(client, Project);

        Assert.Equal(
            [("apply", 1), ("Mixed", 1), ("mixed", 2), ("Zeta", 1)],
            listed.Where(d => d is { Kind: "agent", Scope: "project" }).Select(d => (d.Name, d.Version)));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_module_token_reads_definitions_only_in_a_project_its_module_is_switched_on_for_and_is_refused_every_write()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        string token = await node.TokenAsync(ModuleNode.Module);
        using var module = new DaemonClient(node.Home, token);
        using var owner = new DaemonClient(node.Home);
        IReadOnlyList<DefinitionView> before = await ListAsync(owner, ModuleNode.On);
        DefinitionView any = before[0];

        Assert.Equal(before, await ListAsync(module, ModuleNode.On));
        Assert.Equal(any, (await module.GetAsync<DefinitionDetailView>($"/definitions/{any.Id}?project={ModuleNode.On}", None)).Definition);
        string off = ModuleNode.NotSwitchedOn(ModuleNode.Module, ModuleNode.Off);
        Assert.Equal(off, await node.RefusedAsync(token, HttpMethod.Get, $"/definitions?project={ModuleNode.Off}"));
        Assert.Equal(off, await node.RefusedAsync(token, HttpMethod.Get, $"/definitions/{any.Id}?project={ModuleNode.Off}"));

        string agent = SoftwareDefinitions.Read("agents", "fix");
        Assert.Equal(
            ModuleNode.NotOpen("definition.draft"),
            await node.RefusedAsync(token, HttpMethod.Post, "/definitions", new DefinitionDraftRequest(ModuleNode.On, "agent", agent)));
        Assert.Equal(
            ModuleNode.NotOpen("definition.publish"),
            await node.RefusedAsync(token, HttpMethod.Post, "/definitions/publish", new DefinitionIdRequest(ModuleNode.On, any.Id)));
        Assert.Equal(
            ModuleNode.NotOpen("definition.retire"),
            await node.RefusedAsync(token, HttpMethod.Post, "/definitions/retire", new DefinitionIdRequest(ModuleNode.On, any.Id)));
        Assert.Equal(before, await ListAsync(owner, ModuleNode.On));
    }

    [Fact]
    public async Task A_session_drafts_in_project_scope_in_its_own_project_and_is_refused_owner_scope_another_project_publish_and_retire()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>> { ["build"] = [] }));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await RegisteredProjects.AddAsync(daemon, Other, None);
        await Publishing.FlowAsync(daemon, Project, SingleAgentStep);
        using var owner = new DaemonClient(temp.Home, sessionToken: null);
        ParcelView parcel = await owner.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Drafts a flow", null), None);
        FlowRunView flowRun = await FlowRunDriver.StartAsync(owner, Project, parcel.Id, SingleAgentStep.Name);
        await executor.Collecting.WaitAsync(TestPatience.Bound);
        SessionAccess access = Assert.Single(executor.Inner.Spawns).Access ?? throw new InvalidOperationException("The launch carried no session access.");
        using var session = new DaemonClient(temp.Home, access.Token);
        string rule = $"A session's principal records only on flow run {Ids.Short(flowRun.Id)} in project '{Project}'";
        string agent = AgentDefinition.CanonicalJson(SoftwareAgents.Fix with { Name = "session-fixer" });
        IReadOnlyList<DefinitionView> inProject = await ListAsync(owner, Project);
        IReadOnlyList<DefinitionView> inOther = await ListAsync(owner, Other);
        DefinitionView published = Assert.Single(inProject, d => d.Name == SingleAgentStep.Name);

        DomainException ownerScope = await RefusedAsync(session, new DefinitionDraftRequest(Project, "agent", agent, "owner"));
        Assert.StartsWith(rule, ownerScope.Message, StringComparison.Ordinal);
        DomainException other = await RefusedAsync(session, new DefinitionDraftRequest(Other, "agent", agent));
        Assert.StartsWith(rule, other.Message, StringComparison.Ordinal);
        DomainException publish = await Assert.ThrowsAsync<DomainException>(
            () => session.PostAsync<DefinitionView>("/definitions/publish", new DefinitionIdRequest(Project, published.Id), None));
        Assert.Equal("'definition.publish' is not open to the Agent role.", publish.Message);
        DomainException retire = await Assert.ThrowsAsync<DomainException>(
            () => session.PostAsync<DefinitionView>("/definitions/retire", new DefinitionIdRequest(Project, published.Id), None));
        Assert.Equal("'definition.retire' is not open to the Agent role.", retire.Message);
        Assert.Equal(inProject, await ListAsync(owner, Project));
        Assert.Equal(inOther, await ListAsync(owner, Other));

        DefinitionView drafted = await DraftAsync(session, new DefinitionDraftRequest(Project, "agent", agent));

        Assert.Equal(("project", Project, "draft"), (drafted.Scope, drafted.Project, drafted.Status));
        Assert.Contains(await ListAsync(owner, Project), d => d.Id == drafted.Id);
        await daemon.StopAsync(None);
    }

    private static readonly Flow SingleAgentStep = new()
    {
        Name = "one-agent-step",
        Accepts = ["task"],
        Outcome = "built",
        Budget = 10m,
        SpawnCap = 3,
        Steps =
        [
            new FlowStep
            {
                Name = "build",
                Agent = "build",
                AnswerSchema = "build",
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    private static Task<DefinitionView> DraftAsync(DaemonClient client, DefinitionDraftRequest request) =>
        client.PostAsync<DefinitionView>("/definitions", request, None);

    private static Task<DomainException> RefusedAsync(DaemonClient client, DefinitionDraftRequest request) =>
        Assert.ThrowsAsync<DomainException>(() => DraftAsync(client, request));

    private static async Task<IReadOnlyList<DefinitionView>> ListAsync(DaemonClient client, string project) =>
        await client.GetAsync<List<DefinitionView>>($"/definitions?project={project}", None);

    private static async Task<string> FlowHashAsync(DaemonClient client, string name) =>
        Assert.Single(await client.GetAsync<List<FlowView>>($"/flows?project={Project}", None), f => f.Name == name).Hash;

    /// <summary>
    /// The file <paramref name="json"/> laid out another way: indented, with its top-level properties in
    /// reverse order. Nested objects keep their order, since a dictionary's order can be part of its content.
    /// </summary>
    internal static string Relaid(string json)
    {
        JsonObject file = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("The file is empty.");
        var reversed = new JsonObject(file.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        return reversed.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The flow file <paramref name="flow"/> with its first system step renamed <paramref name="step"/>.</summary>
    internal static string WithSystemStep(string flow, string step)
    {
        JsonNode node = JsonNode.Parse(flow) ?? throw new InvalidOperationException("The flow file is empty.");
        JsonObject first = node["Steps"]?.AsArray().OfType<JsonObject>().First(s => s["SystemStep"] is not null)
            ?? throw new InvalidOperationException("The flow has no system step.");
        first["SystemStep"] = step;
        return node.ToJsonString();
    }
}
