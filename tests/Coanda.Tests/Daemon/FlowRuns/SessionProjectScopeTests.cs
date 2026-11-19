using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Types;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.Modules;
using Coanda.Tests.Daemon.SystemSteps;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// A session principal reads, moves parcels, sets fields, and drafts project definitions only in the
/// project its session is bound to, at every service the registry opens to the Agent role; the owner and
/// the daemon's own Agent-role principals, which carry no session, are answered as before in every project.
/// </summary>
public sealed class SessionProjectScopeTests
{
    private const string Bound = ModuleNode.On;
    private const string Elsewhere = ModuleNode.Off;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    /// <summary>An Agent-role principal with no session, as the trigger loop's and the flow-run runner's are.</summary>
    private static readonly Principal Loop = new("coanda-trigger-loop", Role.Agent);

    private static readonly Principal Session = Principal.ForSession(new SessionBinding(Bound, Ids.New(), Ids.New()));

    [Fact]
    public async Task Every_project_read_refuses_a_session_outside_its_bound_project_and_answers_it_as_the_owner_inside_it()
    {
        await using ModuleNode node = await StartAsync();
        Dictionary<string, (Parcel Parcel, FlowRun FlowRun)> held = [];
        foreach (string project in new[] { Bound, Elsewhere })
        {
            held[project] = await HeldAsync(node, project);
        }

        IServiceProvider services = node.Services;
        ParcelService parcels = services.GetRequiredService<ParcelService>();
        FlowService flows = services.GetRequiredService<FlowService>();
        DefinitionService definitions = services.GetRequiredService<DefinitionService>();
        FlowRunService flowRuns = services.GetRequiredService<FlowRunService>();
        ProjectService projects = services.GetRequiredService<ProjectService>();
        ModuleSettingsService settings = services.GetRequiredService<ModuleSettingsService>();
        TriggerService triggers = services.GetRequiredService<TriggerService>();
        string typeHash = held[Bound].Parcel.TypeHash;

        (string Name, Func<Principal, string, Task<object?>> Read)[] reads =
        [
            ("ParcelService.ListAsync", async (p, project) => await parcels.ListAsync(p, project, None)),
            ("ParcelService.ShowAsync", async (p, project) => await parcels.ShowAsync(p, project, held[project].Parcel.Id, None)),
            ("ParcelService.ShowAtAsync", async (p, project) => await parcels.ShowAtAsync(p, project, held[project].Parcel.Id, 1, None)),
            ("ParcelService.FindByOriginAsync", async (p, project) => await parcels.FindByOriginAsync(p, project, OriginOf(project), None)),
            ("ParcelService.TypesAsync", async (p, project) => await parcels.TypesAsync(p, project, None)),
            ("ParcelService.ResolveTypeAsync", async (p, project) => await parcels.ResolveTypeAsync(p, project, "task", None)),
            ("ParcelService.TypeVersionAsync", async (p, project) => await parcels.TypeVersionAsync(p, project, typeHash, None)),
            ("FlowService.ListAsync", async (p, project) => await flows.ListAsync(p, project, None)),
            ("DefinitionService.ListAgentsAsync", async (p, project) => await definitions.ListAgentsAsync(p, project, None)),
            ("DefinitionService.ResolveAgentAsync", async (p, project) => await definitions.ResolveAgentAsync(p, project, SoftwareAgents.Build.Name, None)),
            ("DefinitionService.ResolveAgentByHashAsync", async (p, project) => await definitions.ResolveAgentByHashAsync(p, project, SoftwareAgents.Build.Hash, None)),
            ("FlowRunService.ListAsync", async (p, project) => await flowRuns.ListAsync(p, project, null, None)),
            ("FlowRunService.ShowAsync", async (p, project) => await flowRuns.ShowAsync(p, project, held[project].FlowRun.Id, None)),
            ("FlowRunService.ChainOfAsync", async (p, project) => await flowRuns.ChainOfAsync(p, project, held[project].FlowRun, None)),
            ("FlowRunService.FlowOfAsync", async (p, project) => await flowRuns.FlowOfAsync(p, project, held[project].FlowRun, None)),
            ("ProjectService.ShowAsync", async (p, project) => await projects.ShowAsync(p, project, None)),
            ("ProjectService.ProfileOfAsync", async (p, project) => new { Profile = await projects.ProfileOfAsync(p, project, None) }),
            ("ModuleSettingsService.ShowAsync", async (p, project) => await settings.ShowAsync(p, project, ModuleNode.Module, None)),
            ("TriggerService.ListSubscriptionsAsync", async (p, project) => await triggers.ListSubscriptionsAsync(p, project, None)),
        ];

        foreach ((string name, Func<Principal, string, Task<object?>> read) in reads)
        {
            object? owned = await read(Owner, Bound);
            Assert.True(owned is not null, $"{name} answered the owner nothing");
            Assert.Equivalent(owned, await read(Session, Bound), strict: true);

            DomainException refused = await Assert.ThrowsAsync<DomainException>(() => read(Session, Elsewhere));
            Assert.True(Refusal(Elsewhere) == refused.Message, $"{name}: {refused.Message}");

            // A daemon loop's principal carries no session and reads in both projects as the owner does.
            Assert.Equivalent(owned, await read(Loop, Bound), strict: true);
            Assert.Equivalent(await read(Owner, Elsewhere), await read(Loop, Elsewhere), strict: true);
        }
    }

    [Fact]
    public async Task A_session_moves_parcels_sets_fields_and_drafts_project_definitions_only_in_its_bound_project_and_a_loop_principal_in_both()
    {
        await using ModuleNode node = await StartAsync();
        ParcelService parcels = node.Services.GetRequiredService<ParcelService>();
        DefinitionService definitions = node.Services.GetRequiredService<DefinitionService>();
        IDocumentStore store = node.Services.GetRequiredService<IDocumentStore>();
        Parcel mine = await node.ParcelAsync(Bound, "Mine");
        Parcel theirs = await node.ParcelAsync(Elsewhere, "Theirs");
        ParcelType drafted = SoftwareTypes.Task with { Fields = [], GateBindings = [] };

        DomainException move = await Assert.ThrowsAsync<DomainException>(() => parcels.MoveAsync(Session, Elsewhere, theirs.Id, "paused", None));
        DomainException set = await Assert.ThrowsAsync<DomainException>(
            () => parcels.SetFieldAsync(Session, Elsewhere, theirs.Id, "objective", FieldValue.OfText("Not mine."), None));
        DomainException draft = await Assert.ThrowsAsync<DomainException>(
            () => definitions.DraftParcelTypeAsync(Session, DefinitionScope.Project, Elsewhere, drafted, null, None));
        Assert.Equal(Refusal(Elsewhere), move.Message);
        Assert.Equal(Refusal(Elsewhere), set.Message);
        Assert.Equal(Refusal(Elsewhere), draft.Message);
        // a refused write appends nothing
        Assert.Single(await EventsAsync(store, Elsewhere, theirs.Id));
        await using (IQuerySession elsewhere = store.QuerySession(Elsewhere))
        {
            Assert.DoesNotContain(await elsewhere.Query<Definition>().ToListAsync(None), d => d.Status == DefinitionStatus.Draft);
        }

        Assert.Equal("paused", (await parcels.MoveAsync(Session, Bound, mine.Id, "paused", None)).Stage);
        Assert.Equal(FieldValue.OfText("Mine."), (await parcels.SetFieldAsync(Session, Bound, mine.Id, "objective", FieldValue.OfText("Mine."), None)).Fields["objective"]);
        Assert.Equal(Session.Id, (await definitions.DraftParcelTypeAsync(Session, DefinitionScope.Project, Bound, drafted, null, None)).DraftedBy);

        // A daemon loop's principal carries no session, and moves and sets fields in both projects.
        foreach (string project in new[] { Bound, Elsewhere })
        {
            Parcel parcel = await node.ParcelAsync(project, "Looped");
            Assert.Equal("paused", (await parcels.MoveAsync(Loop, project, parcel.Id, "paused", None)).Stage);
            Assert.Equal(FieldValue.OfText("Looped."), (await parcels.SetFieldAsync(Loop, project, parcel.Id, "objective", FieldValue.OfText("Looped."), None)).Fields["objective"]);
        }
    }

    [Fact]
    public async Task Listing_projects_answers_a_session_only_its_bound_project_with_that_projects_profile_alone()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        var held = new CredentialProfiles(
        [
            new CredentialProfile("claude-code", "work", "/home/me/.claude-work", null),
            new CredentialProfile("claude-code", "ci", null, "op read op://ci/key"),
        ]);
        var projects = new ProjectService(store, TimeProvider.System, held, ModuleSettingsSchemas.None, ProjectServices.Defaults);
        await RegisteredProjects.AddAsync(store, Bound, None);
        await RegisteredProjects.AddAsync(store, Elsewhere, None);
        await projects.SetProfileAsync(Owner, Bound, "work", None);
        await projects.SetProfileAsync(Owner, Elsewhere, "ci", None);

        Assert.Equal([Bound], (await projects.ListAsync(Session, None)).Select(p => p.Name));
        Assert.Equal(new Dictionary<string, string> { [Bound] = "work" }, await projects.ProfilesAsync(Session, None));

        var both = new Dictionary<string, string> { [Bound] = "work", [Elsewhere] = "ci" };
        foreach (Principal principal in new[] { Owner, Loop })
        {
            Assert.Equal([Bound, Elsewhere], (await projects.ListAsync(principal, None)).Select(p => p.Name));
            Assert.Equal(both, await projects.ProfilesAsync(principal, None));
        }
    }

    /// <summary>The refusal a session bound to <see cref="Bound"/> gets in <paramref name="project"/>, naming both projects.</summary>
    private static string Refusal(string project) =>
        $"{SessionScope.BoundRule(Session.Session ?? throw new InvalidOperationException("The session principal carries no binding."))}; project '{project}' is not project '{Bound}', and a session reads, moves parcels, sets fields, and drafts project definitions only in the project its session is bound to.";

    private static string OriginOf(string project) => $"test:{project}";

    /// <summary>
    /// A node whose test module is switched on for both projects, so every read the owner makes in either
    /// answers rather than refuses, with a subscription in each.
    /// </summary>
    private static async Task<ModuleNode> StartAsync()
    {
        ModuleNode node = await ModuleNode.StartAsync(StandInModules.Schemas);
        try
        {
            await node.SwitchOnAsync(Elsewhere, ModuleNode.Module);
            TriggerService triggers = node.Services.GetRequiredService<TriggerService>();
            foreach (string project in new[] { Bound, Elsewhere })
            {
                await triggers.SubscribeAsync(
                    Owner, project, new Trigger("test/pr-opened", new Dictionary<string, string>()), "task", "number", RunningSystemStep.FlowNamed().Name, None);
            }
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }

        return node;
    }

    /// <summary>A parcel recording an origin in <paramref name="project"/>, and a flow run held on its dispatch on another parcel.</summary>
    private static async Task<(Parcel Parcel, FlowRun FlowRun)> HeldAsync(ModuleNode node, string project)
    {
        Parcel parcel = await node.Services.GetRequiredService<ParcelService>().AddAsync(Owner, project, "task", "Originated", null, OriginOf(project), None);
        (_, Guid flowRunId, _) = await node.DispatchedAsync(project);
        FlowRun flowRun = await node.Services.GetRequiredService<FlowRunService>().ShowAsync(Owner, project, flowRunId, None)
            ?? throw new InvalidOperationException($"No flow run {flowRunId}.");
        return (parcel, flowRun);
    }

    private static async Task<IReadOnlyList<object>> EventsAsync(IDocumentStore store, string project, Guid streamId)
    {
        await using IQuerySession session = store.QuerySession(project);
        var events = await session.Events.FetchStreamAsync(streamId, token: None);
        return [.. events.Select(e => e.Data)];
    }
}
