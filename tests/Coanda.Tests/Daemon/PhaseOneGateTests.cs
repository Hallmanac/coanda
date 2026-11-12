using System.Globalization;
using System.Text.Json.Nodes;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Modules.Software.GitHub;
using Fisher;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The phase 1 gate of the first version plan (section 3): a flow run of the shipped build flow runs end
/// to end on canned answers with every cap enforced and refused at load without them. Every test
/// runs on a throwaway node home with a <see cref="ScriptedExecutor"/>, and none touches a network or a repository.
/// </summary>
public sealed class PhaseOneGateTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static ScriptEntry Costing(string verdict, decimal cost, int ordinal) =>
        new($$"""{"verdict":"{{verdict}}","summary":"answer {{ordinal}}"}""", $"/transcripts/{ordinal}.jsonl", new Measurement(100L * ordinal, cost, 1, TimeSpan.FromSeconds(ordinal)));

    private static ScriptedExecutor Executor(params (string Agent, ScriptEntry[] Entries)[] script) =>
        new(script.ToDictionary(s => s.Agent, s => (IReadOnlyList<ScriptEntry>)s.Entries));

    // 1. The clean path, with no flow start, and the stop at review without a pull request.

    [Fact]
    public async Task A_task_moved_to_build_runs_the_shipped_build_flow_to_its_end_recording_every_hash_and_measurement()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Scripts.CleanBuildExecutor();
        var github = new FakeGitHub();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, GitHubHandler = github.Handler }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);

        // The objective and criteria the move out of refine needs; the open-pr step records the pull request the move out of build needs.
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Through the gate", pullRequest: false);
        Assert.Null(parcel.Fields.Single(f => f.Name == TaskFields.PullRequest).Value);

        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        Assert.Equal("pr-opened", flowRun.Outcome);
        Assert.Equal(SoftwareFlows.Build.Hash, flowRun.FlowHash);

        // The merge follows the build's end with no step taken by hand, merges the head open-pr recorded, and closes the task.
        FlowRunView merge = await FlowRunDriver.MergedAsync(client, Project, parcel.Id, flowRun.Id);
        Assert.Equal(SoftwareFlows.Merge.Hash, merge.FlowHash);
        Assert.Equal("merged", merge.Outcome);
        Assert.Equal(flowRun.Id, merge.ParentFlowRunId);
        Assert.Equal(1, merge.Depth);
        string head = flowRun.Steps.Single(s => s.Step == "open-pr").Result.GetValueOrDefault().GetProperty("head").GetString() ?? string.Empty;
        Assert.NotEmpty(head);
        FakeGitHub.Sent mergeRequest = Assert.Single(github.Requests, r => r.IsMerge);
        Assert.Equal(head, JsonNode.Parse(mergeRequest.Body ?? "{}")?["sha"]?.GetValue<string>());
        Assert.Equal(FakeGitHub.MergeCommit, merge.Steps.Single().Result.GetValueOrDefault().GetProperty("commit").GetString());

        ParcelView after = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None);
        Assert.Equal("closed", after.Stage);
        Assert.Equal("completed", after.Resolution);
        Assert.Null(after.LiveFlowRunId);
        Assert.All(await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id), f => Assert.True(f.Status == "ended"));
        Assert.Equal(FieldValue.OfReference(FakeGitHub.Number.ToString(CultureInfo.InvariantCulture), FakeGitHub.Url), after.Fields.Single(f => f.Name == TaskFields.PullRequest).Value);
        Assert.Equal(1, github.Requests.Count(r => r.IsOpen));
        Assert.Empty(github.Unexpected);
        FlowRunStepView[] agentSteps = [.. flowRun.Steps.Where(s => s.Kind == "agent-step")];
        Assert.Equal(["build", "reviewer-adversarial", "reviewer-conformance"], agentSteps.Select(s => s.Performer));
        foreach ((FlowRunStepView agentStep, int ordinal) in agentSteps.Select((j, i) => (j, i + 1)))
        {
            AgentDefinition agent = SoftwareAgents.All.Single(a => a.Name == agentStep.Performer);
            Assert.Equal(agent.Hash, agentStep.AgentHash);
            Assert.Equal(agent.Model, agentStep.Model);
            // tokens, cost, turns, and duration are recorded as the executor reported them
            Assert.Equal(Scripts.Usage(ordinal), agentStep.Measurement);
        }

        string context = executor.Spawns.First(spawn => spawn.Agent == "build").Context;
        Assert.Contains($"Field objective: {TaskFields.ObjectiveValue.Text}", context, StringComparison.Ordinal);
        foreach (string criterion in TaskFields.CriteriaValue.Items ?? [])
        {
            Assert.Contains($"- {criterion}", context, StringComparison.Ordinal);
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_flow_run_of_a_build_flow_with_no_open_pr_step_on_a_task_with_no_pull_request_pauses_at_its_move_to_review_quoting_the_gate()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow withoutOpenPr = await Publishing.FlowAsync(daemon, Project, WithoutOpenPr(SoftwareFlows.Build));
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "No pull request yet", pullRequest: false);

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        Assert.Equal(withoutOpenPr.Hash, paused.FlowHash);
        Assert.DoesNotContain("open-pr", paused.Steps.Select(s => s.Step));
        Assert.Equal("move-to-review", paused.CurrentStep);
        Assert.Equal($"Step 'move-to-review' could not move parcel {Ids.Short(parcel.Id)} to 'review': The gate 'required-fields' refused the move from 'build' to 'review': a 'task' leaves 'build' only with a value in every field required to leave it, and 'pull-request' has none.", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("move-to-review"), paused.Lever);
        Assert.Equal("build", (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).Stage);

        await daemon.StopAsync(None);
    }

    /// <summary>The build flow with its open-pr step taken out: a clean conformance review goes straight to the move to review.</summary>
    private static Flow WithoutOpenPr(Flow build) => build with
    {
        Steps =
        [
            .. build.Steps
                .Where(s => s.Name != "open-pr")
                .Select(s => s.Name == "conformance-review"
                    ? s with
                    {
                        Transitions =
                        [
                            .. s.Transitions.Select(t => t.Target == "open-pr"
                                ? t with { Target = "move-to-review" }
                                : t),
                        ],
                    }
                    : s),
        ],
    };

    // 2 and 3. The visit cap and the spawn cap.

    /// <summary>The adversarial reviewer never answers clean; every cost is small, so no budget binds.</summary>
    private static ScriptedExecutor NeverClean() => Executor(
        ("build", [Costing("done", 0.5m, 1)]),
        ("reviewer-adversarial", [.. Enumerable.Range(1, 6).Select(n => Costing("not-clean", 0.5m, n))]),
        ("fix", [.. Enumerable.Range(1, 6).Select(n => Costing("done", 0.5m, n))]));

    [Fact]
    public async Task A_review_that_is_never_clean_pauses_a_flow_run_of_the_shipped_build_flow_on_the_cap_it_reaches_first_the_review_to_fix_visit_cap()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = NeverClean();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Never clean");

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        Assert.Equal("Step 'adversarial-review' reached the visit cap of 3 on its transition to 'fix'.", paused.PauseReason);
        Assert.False(string.IsNullOrWhiteSpace(paused.Lever), $"Expected a lever, but found '{paused.Lever}'.");
        Assert.True(paused.Spawns < SoftwareFlows.Build.SpawnCap, $"Expected fewer than {SoftwareFlows.Build.SpawnCap} spawns, but found {paused.Spawns}: the visit cap binds before the spawn cap.");
        Assert.True(paused.Cost < SoftwareFlows.Build.Budget, $"Expected less than {SoftwareFlows.Build.Budget}, but found {paused.Cost}.");
        Assert.Equal(paused.Spawns, executor.Spawns.Count);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_project_copy_of_the_build_flow_whose_spawn_cap_binds_first_pauses_on_the_same_script_naming_the_spawn_cap()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = NeverClean();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow copy = await Publishing.FlowAsync(daemon, Project, SoftwareFlows.Build with { SpawnCap = 4 });
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Never clean, fewer spawns");

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        // the project's copy is what resolution returns for the binding
        Assert.Equal(copy.Hash, paused.FlowHash);
        Assert.Equal("Flow 'build' reached its spawn cap of 4; going to agent step 'fix' would spawn another agent.", paused.PauseReason);
        Assert.False(string.IsNullOrWhiteSpace(paused.Lever), $"Expected a lever, but found '{paused.Lever}'.");
        int reviews = paused.Steps.Count(s => s.Step == "adversarial-review");
        Assert.True(reviews < 4, $"Expected fewer than 4 reviews, but found {reviews}: the visit cap of 3 was never reached.");
        Assert.Equal(4, executor.Spawns.Count);

        await daemon.StopAsync(None);
    }

    // 4. The depth cap on composition.

    [Fact]
    public async Task A_flow_following_its_own_outcome_chains_to_its_depth_cap_and_pauses_the_next_link_before_any_step_even_when_resumed()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Executor(("reviewer-adversarial", [.. Enumerable.Range(1, 5).Select(n => Costing("clean", 0.1m, n))]));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow loop = await Publishing.FlowAsync(daemon, Project, Reviewing("loop", "looped", 5m, new FlowFollow { Outcome = "looped", Flow = "loop", DepthCap = 2 }));
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Round and round", null), None);

        FlowRunView root = await FlowRunDriver.StartAsync(client, Project, parcel.Id, loop.Name);
        FlowRunView capped = await AtDepthAsync(client, parcel.Id, 3);
        capped = await FlowRunDriver.UntilAsync(client, Project, capped.Id, "paused");

        List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id);
        Assert.Equal([0, 1, 2, 3], flowRuns.Select(f => f.Depth));
        Assert.All(flowRuns.Take(3), f => Assert.True(f.Status == "ended" && f.Outcome == "looped"));
        Assert.All(flowRuns, f => Assert.True(f.RootFlowRunId == root.Id));
        Assert.Equal(flowRuns[2].Id, capped.ParentFlowRunId);
        Assert.Equal($"Flow run {Ids.Short(capped.Id)} is at depth 3 of its chain, past the depth cap of 2 on the follow of flow 'loop'; a flow run deeper than its follow's depth cap performs no step.", capped.PauseReason);
        Assert.Equal(FlowRunLevers.DepthCapReached("loop"), capped.Lever);
        FlowRunStepView first = Assert.Single(capped.Steps);
        Assert.Null(first.Answer);
        Assert.Null(first.Result);
        // only the flow runs at depths 0, 1, and 2 spawned
        Assert.Equal(3, executor.Spawns.Count);

        FlowRunView resumed = await FlowRunDriver.ResumeAsync(client, Project, capped.Id, "Go on anyway.");
        FlowRunView again = await PausedAfterAsync(client, capped.Id, resumed.UpdatedAt);

        // a resume is no override of the depth cap
        Assert.Equal(capped.PauseReason, again.PauseReason);
        Assert.Null(Assert.Single(again.Steps).Answer);
        // the depth-capped flow run never spawns
        Assert.Equal(3, executor.Spawns.Count);
        // no flow run starts at depth 4
        Assert.Equal(4, (await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id)).Count);

        await daemon.StopAsync(None);
    }

    // 5. The per-flow-run budget and the budget a chain shares.

    [Fact]
    public async Task A_build_flow_run_whose_first_agent_step_costs_its_whole_budget_pauses_at_its_next_agent_step_without_spawning()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Executor(
            ("build", [Costing("done", SoftwareFlows.Build.Budget, 1)]),
            ("reviewer-adversarial", [Costing("clean", 0.1m, 2)]),
            ("reviewer-conformance", [Costing("clean", 0.1m, 3)]));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Spent at once");

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        Assert.Equal("adversarial-review", paused.CurrentStep);
        Assert.Equal($"Flow run {Ids.Short(paused.Id)} has recorded a cost of $25.00, which reaches the budget of $25.00 of flow 'build'; a flow run performs no agent step once its recorded cost reaches its flow's budget.", paused.PauseReason);
        Assert.Equal(FlowRunLevers.BudgetReached(), paused.Lever);
        Assert.Null(paused.Steps[^1].Answer);
        Assert.Equal(["build"], executor.Spawns.Select(s => s.Agent));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_chain_whose_flow_runs_stay_under_their_own_budgets_pauses_at_its_next_agent_step_once_together_they_reach_the_roots_budget()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Executor(("reviewer-adversarial", [.. Enumerable.Range(1, 4).Select(n => Costing("clean", 0.6m, n))]));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow seed = await Publishing.FlowAsync(daemon, Project, Reviewing("seed", "grown", 1m, follow: null));
        await Publishing.FlowAsync(daemon, Project, Reviewing("grow", "grown", 5m, new FlowFollow { Outcome = "grown", DepthCap = 5 }));
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Shared purse", null), None);

        FlowRunView root = await FlowRunDriver.StartAsync(client, Project, parcel.Id, seed.Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, (await AtDepthAsync(client, parcel.Id, 2)).Id, "paused");

        List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id);
        Assert.Equal([("seed", "ended"), ("grow", "ended"), ("grow", "paused")], flowRuns.Select(f => (f.FlowName, f.Status)));
        Assert.All(flowRuns, f => Assert.True(f.Cost < 1m, "each flow run stays under its own budget"));
        Assert.Equal($"The chain of flow run {Ids.Short(paused.Id)}, rooted at flow run {Ids.Short(root.Id)}, has recorded a cost of $1.20 across its flow runs, which reaches the budget of $1.00 of flow 'seed' that its root flow run follows; a chain performs no agent step once its recorded cost reaches that budget.", paused.PauseReason);
        Assert.Equal(FlowRunLevers.BudgetReached(), paused.Lever);
        Assert.Equal(2, executor.Spawns.Count);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_step_that_pauses_after_its_retry_records_the_summed_usage_and_a_resume_pauses_on_the_budget_without_spawning()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Executor(("build",
        [
            new ScriptEntry(null, "/transcripts/1.jsonl", new Measurement(400, 10m, 2, TimeSpan.FromSeconds(20))),
            new ScriptEntry("not json", "/transcripts/2.jsonl", new Measurement(600, 15m, 3, TimeSpan.FromSeconds(40))),
            Costing("done", 0.1m, 3),
        ]));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Never answers");

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        Assert.Equal("build", paused.CurrentStep);
        Assert.StartsWith("Agent 'build' gave no valid 'build' answer after one retry", paused.PauseReason, StringComparison.Ordinal);
        // the pause records both attempts' usage
        Assert.Equal(new Measurement(1000, 25m, 5, TimeSpan.FromSeconds(60)), paused.PauseUsage);
        Assert.Equal(25m, paused.Cost);

        FlowRunView resumed = await FlowRunDriver.ResumeAsync(client, Project, paused.Id, "Try once more.");
        FlowRunView again = await PausedAfterAsync(client, paused.Id, resumed.UpdatedAt);

        Assert.Equal($"Flow run {Ids.Short(paused.Id)} has recorded a cost of $25.00, which reaches the budget of $25.00 of flow 'build'; a flow run performs no agent step once its recorded cost reaches its flow's budget.", again.PauseReason);
        Assert.Equal(1, again.Steps.Count(s => s.Step == "build"));
        // a resume of a spent flow run spawns nothing
        Assert.Single(executor.Spawns);

        await daemon.StopAsync(None);
    }

    // 6. Refused at load without the caps.

    public static TheoryData<string, string, string> Uncapped => new()
    {
        { "the budget removed", Without(SoftwareFlows.Build, flow => flow.Remove("Budget")), "Flow 'build' needs a budget greater than zero; every flow declares one." },
        { "the spawn cap removed", Without(SoftwareFlows.Build, flow => flow.Remove("SpawnCap")), "Flow 'build' needs a spawn cap greater than zero; every flow declares one." },
        {
            "one transition's visit cap removed",
            Without(SoftwareFlows.Build, flow => Transition(flow, "adversarial-review", "not-clean").Remove("VisitCap")),
            "The transition of step 'adversarial-review' in flow 'build' on 'verdict' needs a visit cap greater than zero; every transition declares one."
        },
        {
            "a follow with no depth cap",
            Without(SoftwareFlows.Build with { Name = "after-build", Follow = new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 } }, flow => flow["Follow"]?.AsObject().Remove("DepthCap")),
            "Flow 'after-build' follows an outcome, so its follow needs a depth cap greater than zero; every composition declares one."
        },
    };

    [Theory]
    [MemberData(nameof(Uncapped))]
    public async Task A_flow_drafted_without_a_cap_is_refused_quoting_the_rule_and_appends_no_event(string missing, string content, string rule)
    {
        Assert.False(string.IsNullOrEmpty(missing));
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);
        Flow read = Flow.FromJson(content);
        long before = await store.Database.FetchHighestEventSequenceNumber(None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => definitions.DraftAsync(
                Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, read.Name, content, read.Hash, null, None));
        Assert.Equal(rule, exception.Message);

        // a refused draft records nothing
        Assert.Equal(before, await store.Database.FetchHighestEventSequenceNumber(None));
    }

    /// <summary>The flow's canonical content with something taken out of it, as a person editing the file might leave it.</summary>
    private static string Without(Flow flow, Action<JsonObject> remove)
    {
        JsonObject content = JsonNode.Parse(Flow.CanonicalJson(flow))?.AsObject()
            ?? throw new InvalidOperationException("A flow's canonical content is a JSON object.");
        remove(content);
        return content.ToJsonString();
    }

    private static JsonObject Transition(JsonObject flow, string step, string verdict) =>
        flow["Steps"]?.AsArray().Single(s => (string?)s?["Name"] == step)?["Transitions"]?.AsArray()
            .Single(t => (string?)t?["Value"] == verdict)?.AsObject()
        ?? throw new InvalidOperationException($"The flow has no transition of '{step}' on '{verdict}'.");

    /// <summary>A flow of one adversarial review that ends on either verdict.</summary>
    private static Flow Reviewing(string name, string outcome, decimal budget, FlowFollow? follow) => new()
    {
        Name = name,
        Accepts = ["task"],
        Outcome = outcome,
        Budget = budget,
        SpawnCap = 1,
        Follow = follow,
        Steps =
        [
            new FlowStep
            {
                Name = "review",
                Agent = "reviewer-adversarial",
                AnswerSchema = "review",
                Transitions =
                [
                    new FlowTransition { Field = "verdict", Value = FlowValue.Of("clean"), VisitCap = 1, Target = Flow.End },
                    new FlowTransition { Field = "verdict", Value = FlowValue.Of("not-clean"), VisitCap = 1, Target = Flow.End },
                ],
            },
        ],
    };

    private static async Task<FlowRunView> AtDepthAsync(DaemonClient client, Guid parcelId, int depth)
    {
        FlowRunView? found = null;
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                found = (await FlowRunDriver.OfParcelAsync(client, Project, parcelId)).SingleOrDefault(f => f.Depth == depth);
                return found is not null;
            },
            $"a flow run at depth {depth} starts on parcel {parcelId}");
        return found ?? throw new InvalidOperationException("The wait ended with no flow run at that depth.");
    }

    /// <summary>Waits for the flow run to pause again after a resume recorded at <paramref name="resumedAt"/>.</summary>
    private static async Task<FlowRunView> PausedAfterAsync(DaemonClient client, Guid flowRunId, DateTimeOffset resumedAt)
    {
        FlowRunView? found = null;
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                found = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
                return found is { Status: "paused" } && found.UpdatedAt > resumedAt;
            },
            $"flow run {flowRunId} pauses again after its resume");
        return found ?? throw new InvalidOperationException("The wait ended with no flow run.");
    }
}
