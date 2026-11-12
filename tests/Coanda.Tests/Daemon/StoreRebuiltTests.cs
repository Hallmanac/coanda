using System.Text.Json;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Modules.Software.GitHub;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A node home the export-and-rebuild build rebuilt starts under this build and reads through the
/// daemon's services as the home main wrote at e86428f read before the rebuild. The fixture is that
/// rebuilt home's database as <c>sqlite3 .dump</c> wrote it (<see cref="RecordHomes.RebuiltByEe75e50"/>):
/// main seeded the source home over a store holding the build flow as the last build before the step
/// prefix shipped it, so the build flow is at version 2; registered alpha; published in alpha a fork of
/// build version 2 with a budget of 30, and in owner scope a fork of fix version 1 whose model's effort is
/// max; and added a task in alpha. The export-and-rebuild build's own test then added a flow run of alpha's
/// build flow recorded with unprefixed performers and the gated-build flow, whose content names the steps
/// unprefixed, and a second flow run under gated-build recorded the same way.
/// </summary>
public sealed class StoreRebuiltTests
{
    private const string Alpha = "alpha";

    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Main's build flow, as the fixture's version 2 of it holds it, before the review diff step.</summary>
    private static readonly Flow MainBuild = PreviousFlows.BuildBeforeReviewDiff;

    private static readonly Flow AlphaBuild = MainBuild with { Budget = 30m };

    private static readonly Flow GatedBuild = PreviousFlows.BuildWithGatedMerge with { Name = RecordHomes.GatedBuild };

    /// <summary>
    /// The content hash each shipped agent was stored under at 0.1.0, before agents carried their answer
    /// schema as content; a start imports the content they carry now as each one's next version.
    /// </summary>
    private static readonly Dictionary<string, string> StoredAgentHashes = new(StringComparer.Ordinal)
    {
        ["build"] = "37dd84328f49ca25",
        ["discovery"] = "e99e12f13d399792",
        ["fix"] = "4ca4afe60ac4e100",
        ["reviewer-adversarial"] = "4ff7ac20b180df0d",
        ["reviewer-conformance"] = "9f867144783e855c",
    };

    /// <summary>
    /// The hash the owner-scope fork of fix was stored with: fix of 0.1.0, naming its schema and holding
    /// no content, with the effort max.
    /// </summary>
    private const string OwnerFixHash = "173a17648aba437b";

    private static readonly Guid BuildFlowRun = Guid.Parse("01a1ad6c-bc0b-727b-a228-9e5042800001");

    private static readonly Guid GatedFlowRun = Guid.Parse("01a1ad6c-bc0b-727b-a228-9e5042800011");

    /// <summary>Restores the rebuilt home and starts the daemon on it with a scripted command runner and a fake GitHub, so nothing touches a remote.</summary>
    private static async Task<RunningDaemon> StartAsync(TemporaryHome temp)
    {
        RecordHomes.Restore(temp.Home, RecordHomes.RebuiltByEe75e50);
        return await Daemons.StartAsync(temp.Home, new DaemonOptions { CommandRunner = new ScriptedCommandRunner(), GitHubHandler = new FakeGitHub().Handler }, None);
    }

    private static List<(DefinitionKind Kind, string Name, string Hash)> Expected(Flow build, params Flow[] more) =>
    [
        .. SoftwareTypes.All.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => (DefinitionKind.ParcelType, t.Name, t.Hash)),
        .. SoftwareFlows.All.Select(f => f.Name == "build" ? build : f).Concat(more).OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => (DefinitionKind.Flow, f.Name, f.Hash)),
        .. SoftwareAgents.All.OrderBy(a => a.Name, StringComparer.Ordinal).Select(a => (DefinitionKind.Agent, a.Name, a.Name == "fix" ? OwnerFixHash : a.Hash)),
    ];

    private static async Task<List<(DefinitionKind Kind, string Name, string Hash)>> ResolvedAsync(IDocumentStore store, string project)
    {
        var resolver = new DefinitionResolver(store);
        List<(DefinitionKind, string, string)> resolved = [];
        foreach (DefinitionKind kind in Enum.GetValues<DefinitionKind>())
        {
            resolved.AddRange((await resolver.ResolveAllAsync(project, kind, None)).Select(d => (d.Kind, d.Name, d.ContentHash)));
        }

        return resolved;
    }

    private static async Task<List<Definition>> HeldAsync(IDocumentStore store)
    {
        List<Definition> held = [];
        foreach (string tenant in new[] { Tenants.Node, Alpha })
        {
            await using IQuerySession session = store.QuerySession(tenant);
            held.AddRange(await session.Query<Definition>().ToListAsync(None));
        }

        return held;
    }

    [Fact]
    public async Task A_rebuilt_home_starts_and_reads_its_project_with_its_repo_and_the_software_settings_the_rebuild_carried()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp);

        Project alpha = Assert.Single(await daemon.Services.GetRequiredService<ProjectService>().ListAsync(Principal.LocalOwner, None));
        Assert.Equal(Alpha, alpha.Name);
        Assert.Equal([new ProjectRepo("repo", "https://github.com/example/repo.git")], alpha.Repos);

        ModuleSettingsShown settings = await daemon.Services.GetRequiredService<ModuleSettingsService>().ShowAsync(Principal.LocalOwner, Alpha, SoftwareSteps.Module, None);
        JsonElement? primary = Assert.Single(settings.Replicated, s => s.Name == "primary-branch").Value;
        JsonElement? checks = Assert.Single(settings.Replicated, s => s.Name == "required-checks").Value;
        Assert.Equal("""{"repo":"main"}""", primary?.GetRawText());
        Assert.Equal("""["build-test (ubuntu-latest)"]""", checks?.GetRawText());
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_rebuilt_home_resolves_the_definitions_main_recorded_with_their_content_hashes_and_versions_and_none_lost()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();

        Assert.Equal(Expected(AlphaBuild, GatedBuild), await ResolvedAsync(store, Alpha));
        Assert.Equal(Expected(SoftwareFlows.Build), await ResolvedAsync(store, "beta"));

        // The fixture's fifteen definitions, the task type a start imports as its next version, since it
        // has gained the repos field since main wrote the home, the five agents it imports as their
        // next versions, since they have come to carry their answer schema content, and the build flow
        // it imports as its next version, since it has gained the review diff step.
        List<Definition> held = await HeldAsync(store);
        Assert.Equal(22, held.Count);
        Definition task = held.Where(d => d.Kind == DefinitionKind.ParcelType && d.Name == "task").MaxBy(d => d.VersionNumber)
            ?? throw new InvalidOperationException("No task type.");
        Assert.Equal((DefinitionScope.Module, SoftwareTypes.Task.Hash, DefinitionStatus.Published), (task.Scope, task.ContentHash, task.Status));
        List<Definition> agents = [.. held.Where(d => d.Kind == DefinitionKind.Agent && d.Scope == DefinitionScope.Module)];
        Assert.All(SoftwareAgents.All, shipped => Assert.Equal(
            [(1, StoredAgentHashes[shipped.Name], DefinitionStatus.Published), (2, shipped.Hash, DefinitionStatus.Published)],
            agents.Where(d => d.Name == shipped.Name).OrderBy(d => d.VersionNumber).Select(d => (d.VersionNumber, d.ContentHash, d.Status))));
        Definition build = held.Where(d => d.Kind == DefinitionKind.Flow && d.Name == "build" && d.Scope == DefinitionScope.Module).MaxBy(d => d.VersionNumber)
            ?? throw new InvalidOperationException("No build flow.");
        Assert.Equal((3, SoftwareFlows.Build.Hash, DefinitionStatus.Published), (build.VersionNumber, build.ContentHash, build.Status));
        Definition[] started = [task, build, .. agents.Where(d => d.VersionNumber == 2)];

        // What main recorded in shipped scope the rebuild wrote as the software module's, at version 0.1.0, with the content and hash it was stored with.
        List<Definition> recorded = [.. held.Where(d => !started.Contains(d) && d.Scope == DefinitionScope.Module)];
        Assert.Equal(12, recorded.Count);
        Assert.All(recorded, d => Assert.Equal(("software", "0.1.0"), (d.Module, d.ModuleVersion)));
        Assert.All(held.Where(d => d.Scope != DefinitionScope.Module), d => Assert.Equal((null, null), (d.Module, d.ModuleVersion)));
        List<Definition> builds = [.. recorded.Where(d => d.Kind == DefinitionKind.Flow && d.Name == "build").OrderBy(d => d.VersionNumber)];
        Assert.Equal([1, 2], builds.Select(d => d.VersionNumber));
        Assert.Equal(MainBuild.Hash, builds[1].ContentHash);
        Assert.Equal(Flow.CanonicalJson(MainBuild), Flow.CanonicalJson(Flow.FromJson(builds[1].Content)));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_fork_main_recorded_reads_its_base_in_the_current_shape_and_resolves_it_to_the_same_module_scope_content()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp);
        var resolver = new DefinitionResolver(daemon.Services.GetRequiredService<IDocumentStore>());
        Definition alphaBuild = await resolver.ResolveAsync(Alpha, DefinitionKind.Flow, "build", None) ?? throw new InvalidOperationException("No build flow in alpha.");
        Definition ownerFix = await resolver.ResolveAsync(Alpha, DefinitionKind.Agent, "fix", None) ?? throw new InvalidOperationException("No fix agent in alpha.");

        Assert.Equal(
            (DefinitionScope.Project, 1, new ForkBase("build", "software", "0.1.0", MainBuild.Hash), AlphaBuild.Hash),
            (alphaBuild.Scope, alphaBuild.VersionNumber, alphaBuild.ForkBase, alphaBuild.ContentHash));
        Definition buildBase = await resolver.ForkedFromAsync(alphaBuild, None) ?? throw new InvalidOperationException("The fork's base resolves to nothing.");
        Assert.Equal((DefinitionScope.Module, 2, MainBuild.Hash, "software", "0.1.0"), (buildBase.Scope, buildBase.VersionNumber, buildBase.ContentHash, buildBase.Module, buildBase.ModuleVersion));

        Assert.Equal(
            (DefinitionScope.Owner, 1, new ForkBase("fix", "software", "0.1.0", StoredAgentHashes["fix"]), OwnerFixHash),
            (ownerFix.Scope, ownerFix.VersionNumber, ownerFix.ForkBase, ownerFix.ContentHash));
        Definition fixBase = await resolver.ForkedFromAsync(ownerFix, None) ?? throw new InvalidOperationException("The fork's base resolves to nothing.");
        Assert.Equal((DefinitionScope.Module, 1, StoredAgentHashes["fix"], "software", "0.1.0"), (fixBase.Scope, fixBase.VersionNumber, fixBase.ContentHash, fixBase.Module, fixBase.ModuleVersion));

        // The fork reads, and resolves its answer schema by the name it holds, as when it was stored.
        AgentDefinition fork = AgentDefinition.FromJson(ownerFix.Content);
        Assert.Equal(OwnerFixHash, fork.Hash);
        Assert.Null(fork.AnswerSchemaContent);
        Assert.Same(NameOnlyAnswerSchemas.Find(NameOnlyAnswerSchemas.Fix), fork.ResolveAnswerSchema());
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_parcel_main_created_still_reads_the_type_it_was_created_under()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp);

        Parcel parcel = Assert.Single(await daemon.Services.GetRequiredService<ParcelService>().ListAsync(Principal.LocalOwner, Alpha, None));
        ParcelType? createdUnder = await new DefinitionResolver(daemon.Services.GetRequiredService<IDocumentStore>()).ResolveParcelTypeByHashAsync(Alpha, parcel.TypeHash, None);

        // Created under the task type main had, before the repos field, which it still reads.
        Assert.Equal(SoftwareTypes.UnownedTask.Hash, parcel.TypeHash);
        Assert.Equal(SoftwareTypes.UnownedTask.Hash, createdUnder?.Hash);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Each_flow_run_reads_its_system_steps_under_their_software_names_and_the_unprefixed_flow_keeps_its_content_and_hash()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();
        IReadOnlyList<FlowRun> listed = await flowRuns.ListAsync(Principal.LocalOwner, Alpha, null, None);
        Assert.Equal([BuildFlowRun, GatedFlowRun], listed.Select(f => f.Id).Order());

        FlowRun build = Assert.Single(listed, f => f.Id == BuildFlowRun);
        Assert.Equal(("build", 1, AlphaBuild.Hash, FlowRunStatus.Paused), (build.FlowName, build.FlowVersion, build.FlowHash, build.Status));
        Assert.Equal([("worktree", SoftwareSteps.Worktree)], build.Steps.Select(s => (s.Step, s.Performer)));

        FlowRun gated = Assert.Single(listed, f => f.Id == GatedFlowRun);
        Assert.Equal((RecordHomes.GatedBuild, 1, GatedBuild.Hash, FlowRunStatus.Stopped), (gated.FlowName, gated.FlowVersion, gated.FlowHash, gated.Status));
        Assert.Equal([("worktree", SoftwareSteps.Worktree), ("gates", SoftwareSteps.Gates)], gated.Steps.Select(s => (s.Step, s.Performer)));

        // The flow version the gated flow run started under names the steps unprefixed, with the hash it was published with.
        Definition flow = await new DefinitionResolver(daemon.Services.GetRequiredService<IDocumentStore>()).ResolveAsync(Alpha, DefinitionKind.Flow, RecordHomes.GatedBuild, None)
            ?? throw new InvalidOperationException("No gated-build flow in alpha.");
        Assert.Equal((1, GatedBuild.Hash), (flow.VersionNumber, flow.ContentHash));
        Assert.Equal(Flow.CanonicalJson(GatedBuild), Flow.CanonicalJson(Flow.FromJson(flow.Content)));
        IReadOnlyList<string> named = [.. Flow.FromJson(flow.Content).Steps.Select(s => s.SystemStep).OfType<string>()];
        Assert.Subset(named.ToHashSet(), new HashSet<string>(["worktree", "gates", "open-pr", "gated-merge"]));
        Assert.DoesNotContain(named, s => s.StartsWith($"{SoftwareSteps.Module}/", StringComparison.Ordinal));
        await daemon.StopAsync(None);
    }
}
