using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Modules.Software.Repositories;
using Coanda.Modules.Software.Steps;
using Coanda.Sdk.Repositories;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Modules.Software.GitHub;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Modules.Software;

/// <summary>
/// What a node home written before the software module was carved out holds keeps working: its clones
/// and work paths are where the module looks, the branch, owner, and note names come out the same, and
/// a flow run that started under a flow version naming the software steps without the prefix runs them
/// on the module, reads the heads they recorded, and writes the pull request field. Such a flow version
/// keeps its content and hash, while every step its flow runs record from this build on, and every
/// visit an earlier build recorded, reads by the step's prefixed name.
/// </summary>
public sealed class UnprefixedStepTests
{
    private const string Project = SoftwareNode.Project;
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A flow as main wrote it, naming the steps unprefixed, so its flow runs can hold results recorded under those names.</summary>
    private static readonly Flow Recorder = new()
    {
        Name = "recorder",
        Accepts = ["task"],
        Outcome = "recorded",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            Unprefixed("open-pr", "opened", "rebase"),
            Unprefixed("rebase", "done", "gated-merge"),
            Unprefixed("gated-merge", "merged", "worktree"),
            Unprefixed("worktree", "done", Flow.End),
        ],
    };

    [Fact]
    public void Work_paths_stay_where_main_keeps_them_and_clones_move_to_the_per_repo_layout()
    {
        var home = new NodeHome(Path.Combine(Path.GetTempPath(), "coanda-home"));
        Guid flowRunId = Ids.New();

        Assert.Equal(Path.Combine(home.Path, "work", Project, flowRunId.ToString()), home.FlowRunWorkPath(Project, flowRunId));
        Assert.Equal(Path.Combine(home.Path, "clones"), home.EarlierClonesPath);
        Assert.Equal(Path.Combine(home.Path, "projects", Project, "repos", "repo"), home.RepoClonePath(Project, "repo"));
        Assert.Equal(home.RepoClonePath(Project, "repo"), RepoLayout.ClonePath(home.Path, Project, "repo"));
    }

    [Fact]
    public void The_branch_its_owner_setting_and_the_rebase_note_keep_the_names_main_gives_them()
    {
        Guid parcelId = Guid.Parse("01a1aa2b-396f-76c0-b4f7-561db6135f29");
        Guid flowRunId = Guid.Parse("01a1aa2b-396f-76c0-b4f7-561db6135f30");

        Assert.Equal("coanda/b6135f29", ProjectClones.BranchOf(parcelId));
        Assert.Equal($"coanda/{Ids.Short(parcelId)}", ProjectClones.BranchOf(parcelId));
        Assert.Equal("branch.coanda/b6135f29.coandaParcel", ProjectClones.OwnerKeyOf(ProjectClones.BranchOf(parcelId)));
        Assert.Equal("refs/coanda/rebase/01a1aa2b-396f-76c0-b4f7-561db6135f30", ProjectClones.NoteRefOf(flowRunId));
    }

    [Fact]
    public async Task A_build_flow_run_under_a_version_naming_the_steps_unprefixed_runs_them_on_the_module_and_open_pr_sets_the_pull_request_field()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await PreviousFlows.PublishAsync(Store(daemon), Project, PreviousFlows.Build);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Built as main built it");

        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        Assert.Equal(PreviousFlows.Build.Hash, ended.FlowHash);
        Assert.Equal(
            [SoftwareSteps.Worktree, SoftwareSteps.Gates, SoftwareSteps.OpenPr],
            ended.Steps.Where(s => s.Kind == "system-step" && s.Performer != CoreSystemSteps.MoveParcel).Select(s => s.Performer));
        Assert.Equal(OpenPrStep.OpenedVerdict, ended.Steps.Single(s => s.Step == "open-pr").Result?.GetProperty("verdict").GetString());
        ParcelView shown = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None);
        Assert.Equal(new FieldReference(FakeGitHub.Number.ToString(), FakeGitHub.Url), shown.Fields.Single(f => f.Name == TaskFields.PullRequest).Value?.Reference);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_step_entered_and_dispatched_before_the_change_is_dispatched_again_under_its_id_on_the_module_after_it()
    {
        using var temp = new TemporaryHome();
        Guid flowRunId;
        Guid dispatchId;
        int seeded;
        string database = temp.Home.EnsureExists().EnsureDatabase().DatabasePath;
        await using (IDocumentStore store = await SeededStore.OpenAsync(database, SoftwareDefinitions.All))
        {
            // What main leaves when its daemon stops while the worktree step of a build flow run is dispatched,
            // appended as main appended it rather than through this build's flow run service.
            await RegisteredProjects.AddAsync(store, Project, None);
            await PreviousFlows.PublishAsync(store, Project, PreviousFlows.Build);
            var parcels = new ParcelService(store, TimeProvider.System);
            Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Interrupted", null, None);
            await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, pullRequest: false, cancellationToken: None);
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            (flowRunId, dispatchId) = await EarlierFlowRuns.DispatchedAsync(store, Project, parcel.Id, None);
            seeded = EarlierFlowRuns.Rows(database, flowRunId).Count;
            Assert.Equal(["worktree", "worktree"], EarlierFlowRuns.Rows(database, flowRunId).Select(EarlierFlowRuns.SystemStepOf));

            // Read from the stored document and by replaying the stream, the visit names the step prefixed.
            await using IQuerySession session = store.QuerySession(Project);
            FlowRun stored = await session.LoadAsync<FlowRun>(flowRunId, None) ?? throw new InvalidOperationException("The flow run has no document.");
            FlowRun replayed = await session.Events.AggregateStreamAsync<FlowRun>(flowRunId, token: None) ?? throw new InvalidOperationException("The flow run has no events.");
            Assert.Equal((SoftwareSteps.Worktree, dispatchId), (stored.Current?.Performer, stored.Current?.DispatchId));
            Assert.Equal((SoftwareSteps.Worktree, dispatchId), (replayed.Current?.Performer, replayed.Current?.DispatchId));
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var client = new DaemonClient(temp.Home);

        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        FlowRunStepView worktree = ended.Steps.First(s => s.Step == "worktree");
        Assert.Equal(SoftwareSteps.Worktree, worktree.Performer);
        Assert.Equal(Results.DoneVerdict, worktree.Result?.GetProperty("verdict").GetString());
        FlowRun recorded = await new FlowRunService(Store(daemon), TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowRunId, None)
            ?? throw new InvalidOperationException("The flow run is gone.");
        Assert.Equal(dispatchId, recorded.Steps.First(s => s.Step == "worktree").DispatchId);

        // The seed's rows are as main wrote them, and every row the daemon appended names a system step by its current name.
        IReadOnlyList<StoredRow> rows = EarlierFlowRuns.Rows(database, flowRunId);
        Assert.Equal(["worktree", "worktree"], rows.Take(seeded).Select(EarlierFlowRuns.SystemStepOf));
        IReadOnlyList<string?> appended = [.. rows.Skip(seeded).Select(EarlierFlowRuns.SystemStepOf).OfType<string>()];
        Assert.Contains(SoftwareSteps.Worktree, appended);
        Assert.DoesNotContain(appended, step => SoftwareSteps.Unprefixed.Contains(step));
        Assert.Equal(SoftwareSteps.Worktree, EarlierFlowRuns.SystemStepOf(rows.Skip(seeded).First(r => r.Type == "system_step_completed")));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_merges_head_guard_reads_the_head_open_pr_recorded_under_its_unprefixed_name()
    {
        var github = new FakeGitHub();
        await using SoftwareNode node = await LegacyNodeAsync(github: github);
        Parcel parcel = await node.TaskAsync("Merge me");
        await node.Parcels.SetFieldAsync(Principal.LocalOwner, Project, parcel.Id, TaskFields.PullRequest, FieldValue.OfReference("17", FakeGitHub.Url), None);
        Guid flowRunId = await RecordedAsync(node, parcel.Id, "open-pr", ScriptedCommandRunner.PrimaryHead);
        await node.FlowRuns.StartStepAsync(Principal.LocalOwner, Project, flowRunId, "gated-merge", None);

        SystemStepAnswer outcome = await node.DispatchAsync(SoftwareSteps.GatedMerge, parcel.Id, flowRunId);

        Assert.Equal(GatedMergeStep.MergedVerdict, Assert.IsType<SystemStepAnswer.Done>(outcome).Result.GetProperty("verdict").GetString());
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, JsonDocument.Parse(Assert.Single(github.Requests, r => r.IsMerge).Body ?? "{}").RootElement.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task The_rebase_reads_the_head_open_pr_recorded_under_its_unprefixed_name()
    {
        const string Recorded = "aaaabbbbccccddddeeeeffff0000111122223333";
        var runner = new ScriptedCommandRunner();
        await using SoftwareNode node = await LegacyNodeAsync(runner);
        Parcel parcel = await node.TaskAsync("Rebase me");
        Guid flowRunId = await RecordedAsync(node, parcel.Id, "open-pr", Recorded);
        await node.FlowRuns.StartStepAsync(Principal.LocalOwner, Project, flowRunId, "rebase", None);
        string clone = node.Home.ProjectClonePath(Project);
        string branch = ProjectClones.BranchOf(parcel.Id);
        runner.AddBranch(clone, branch, Recorded);
        runner.AddWorktree(clone, node.Home.FlowRunWorkPath(Project, flowRunId), branch, Recorded);
        runner.SetRemoteBranch(clone, branch, Recorded);
        runner.SetTrackingRef(clone, ProjectClones.TrackingRefOf(branch), Recorded);

        SystemStepAnswer outcome = await node.DispatchAsync(SoftwareSteps.Rebase, parcel.Id, flowRunId);

        Assert.Equal(
            ScriptedCommandRunner.RebasedOnto(Recorded, ScriptedCommandRunner.PrimaryHead),
            Assert.IsType<SystemStepAnswer.Done>(outcome).Result.GetProperty("head").GetString());
        Assert.Contains($"checkout --quiet --detach {Recorded}", runner.GitCalls);
    }

    [Fact]
    public async Task The_merges_head_guard_reads_the_head_rebase_recorded_under_its_unprefixed_name()
    {
        const string Opened = "aaaabbbbccccddddeeeeffff0000111122223333";
        var github = new FakeGitHub();
        await using SoftwareNode node = await LegacyNodeAsync(github: github);
        Parcel parcel = await node.TaskAsync("Merge me");
        await node.Parcels.SetFieldAsync(Principal.LocalOwner, Project, parcel.Id, TaskFields.PullRequest, FieldValue.OfReference("17", FakeGitHub.Url), None);
        Guid flowRunId = await RecordedAsync(node, parcel.Id, "open-pr", Opened);
        await node.FlowRuns.StartStepAsync(Principal.LocalOwner, Project, flowRunId, "rebase", None);
        await Record(node, flowRunId, "rebase", ScriptedCommandRunner.PrimaryHead);
        await node.FlowRuns.StartStepAsync(Principal.LocalOwner, Project, flowRunId, "gated-merge", None);

        SystemStepAnswer outcome = await node.DispatchAsync(SoftwareSteps.GatedMerge, parcel.Id, flowRunId);

        // the unprefixed rebase's head, recorded after open-pr's, is the one the merge holds the pull request to
        Assert.Equal(GatedMergeStep.MergedVerdict, Assert.IsType<SystemStepAnswer.Done>(outcome).Result.GetProperty("verdict").GetString());
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, JsonDocument.Parse(Assert.Single(github.Requests, r => r.IsMerge).Body ?? "{}").RootElement.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task A_module_writes_under_a_dispatch_of_one_of_the_five_unprefixed_steps_and_of_no_other_unprefixed_step()
    {
        await using SoftwareNode node = await LegacyNodeAsync();
        Flow deploying = new()
        {
            Name = "deploying",
            Accepts = ["task"],
            Outcome = "deployed",
            Budget = 1m,
            SpawnCap = 1,
            Steps = [Unprefixed("deploy", "done", Flow.End)],
        };
        await PublishKnowingAsync(node, deploying, "deploy");
        Principal module = Principal.ForModule(SoftwareSteps.Module);
        FieldValue value = FieldValue.OfReference("17", FakeGitHub.Url);

        Parcel legacy = await node.TaskAsync("Opened as main opened it");
        (Guid legacyRun, Guid legacyDispatch) = await HeldAsync(node, legacy.Id, Recorder.Name, "open-pr");
        Parcel other = await node.TaskAsync("Deployed");
        (Guid otherRun, Guid otherDispatch) = await HeldAsync(node, other.Id, deploying.Name, "deploy");

        Parcel written = await node.Parcels.SetFieldAsync(module, Project, legacy.Id, TaskFields.PullRequest, value, new NamedDispatch(legacyRun, legacyDispatch), None);
        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => node.Parcels.SetFieldAsync(module, Project, other.Id, TaskFields.PullRequest, value, new NamedDispatch(otherRun, otherDispatch), None));

        Assert.Equal(value.Reference, written.ValueOf(TaskFields.PullRequest)?.Reference);
        Assert.Contains("performs the system step 'deploy', which is not module 'software''s", refused.Message, StringComparison.Ordinal);
    }

    private static FlowStep Unprefixed(string step, string verdict, string target) => new()
    {
        Name = step,
        SystemStep = step,
        Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of(verdict), VisitCap = 1, Target = target }],
    };

    /// <summary>A node with <see cref="Recorder"/> published in its project as main would have published it.</summary>
    private static async Task<SoftwareNode> LegacyNodeAsync(ScriptedCommandRunner? runner = null, FakeGitHub? github = null)
    {
        SoftwareNode node = await SoftwareNode.StartAsync(runner, github);
        await PreviousFlows.PublishAsync(node.Store, Project, Recorder);
        return node;
    }

    /// <summary>Starts a flow run of <see cref="Recorder"/> on the parcel, records a result of <paramref name="step"/> carrying <paramref name="head"/> given under the step's unprefixed name, and gives its id.</summary>
    private static async Task<Guid> RecordedAsync(SoftwareNode node, Guid parcelId, string step, string head)
    {
        FlowRun started = await node.FlowRuns.StartAsync(Principal.LocalOwner, Project, parcelId, Recorder.Name, None);
        await Record(node, started.Id, step, head);
        return started.Id;
    }

    private static async Task Record(SoftwareNode node, Guid flowRunId, string step, string head)
    {
        JsonElement result = JsonSerializer.SerializeToElement(new { verdict = step == "open-pr" ? "opened" : "done", systemStep = step, number = 17, url = FakeGitHub.Url, head });
        FlowRun recorded = await node.FlowRuns.RecordResultAsync(Principal.LocalOwner, Project, flowRunId, step, step, result, None);
        Assert.Equal(SoftwareSteps.Canonical(step), recorded.Steps.Last(s => s.Step == step).Performer);
    }

    /// <summary>Starts a flow run of <paramref name="flow"/> on the parcel, moves it into <paramref name="step"/>, and records a dispatch it then holds.</summary>
    private static async Task<(Guid FlowRunId, Guid DispatchId)> HeldAsync(SoftwareNode node, Guid parcelId, string flow, string step)
    {
        FlowRunService flowRuns = node.FlowRuns;
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcelId, flow, None);
        if (started.CurrentStep != step)
        {
            await flowRuns.StartStepAsync(Principal.LocalOwner, Project, started.Id, step, None);
        }

        FlowRun dispatched = await flowRuns.DispatchedAsync(Principal.LocalOwner, Project, started.Id, step, None);
        return (started.Id, dispatched.Current?.DispatchId ?? throw new InvalidOperationException("No dispatch was recorded."));
    }

    private static async Task PublishKnowingAsync(SoftwareNode node, Flow flow, string systemStep)
    {
        var definitions = new DefinitionService(
            node.Store, TimeProvider.System, systemSteps: new HashSet<string>([.. PreviousFlows.SystemSteps, systemStep], StringComparer.Ordinal));
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, flow.Name, Flow.CanonicalJson(flow), flow.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
    }

    private static IDocumentStore Store(RunningDaemon daemon) => daemon.Services.GetRequiredService<IDocumentStore>();
}
