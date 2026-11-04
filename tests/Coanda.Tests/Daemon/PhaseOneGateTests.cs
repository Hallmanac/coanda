using System.Text.Json.Nodes;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Contours;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Flows;
using Coanda.Tests.Daemon.Flows;
using Fisher;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The phase 1 gate of the prototype plan (section 3): a flow of the shipped build contour runs end
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

    // 1. The clean path, with no contour start, and the stop at review without a pull request.

    [Fact]
    public async Task A_task_moved_to_build_runs_the_shipped_build_contour_to_its_end_recording_every_hash_and_measurement()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Scripts.CleanBuildExecutor();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);

        // The objective and criteria the move out of refine needs, and the pull request the move out of build needs.
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Through the gate", pullRequest: true);
        parcel.Fields.Where(f => f.Name != TaskFields.NonGoals).Should().OnlyContain(f => f.Value != null);

        FlowView flow = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        flow.Outcome.Should().Be("pr-opened");
        flow.ContourHash.Should().Be(ShippedContours.Build.Hash);
        (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).State.Should().Be("review");
        FlowStepView[] judgments = [.. flow.Steps.Where(s => s.Kind == "judgment")];
        judgments.Select(s => s.Performer).Should().Equal("build", "reviewer-adversarial", "reviewer-conformance");
        foreach ((FlowStepView judgment, int ordinal) in judgments.Select((j, i) => (j, i + 1)))
        {
            AgentDefinition agent = ShippedAgents.All.Single(a => a.Name == judgment.Performer);
            judgment.AgentHash.Should().Be(agent.Hash);
            judgment.Model.Should().Be(agent.Model);
            judgment.Measurement.Should().Be(Scripts.Usage(ordinal), "tokens, cost, turns, and duration are recorded as the executor reported them");
        }

        string context = executor.Spawns.First(spawn => spawn.Agent == "build").Context;
        context.Should().Contain($"Field objective: {TaskFields.ObjectiveValue.Text}");
        foreach (string criterion in TaskFields.CriteriaValue.Items ?? [])
        {
            context.Should().Contain($"- {criterion}");
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_flow_of_the_shipped_build_contour_on_a_task_with_no_pull_request_pauses_at_its_move_to_review_quoting_the_gate()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "No pull request yet", pullRequest: false);

        FlowView paused = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        paused.CurrentStep.Should().Be("move-to-review");
        paused.PauseReason.Should().Be(
            $"Step 'move-to-review' could not move parcel {Ids.Short(parcel.Id)} to 'review': The gate 'required-fields' refused the move from 'build' to 'review': a 'task' leaves 'build' only with a value in every field required to leave it, and 'pull-request' has none.");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("move-to-review"));
        (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).State.Should().Be("build");

        await daemon.StopAsync(None);
    }

    // 2 and 3. The visit cap and the spawn cap.

    /// <summary>The adversarial reviewer never answers clean; every cost is small, so no budget binds.</summary>
    private static ScriptedExecutor NeverClean() => Executor(
        ("build", [Costing("done", 0.5m, 1)]),
        ("reviewer-adversarial", [.. Enumerable.Range(1, 6).Select(n => Costing("not-clean", 0.5m, n))]),
        ("fix", [.. Enumerable.Range(1, 6).Select(n => Costing("done", 0.5m, n))]));

    [Fact]
    public async Task A_review_that_is_never_clean_pauses_a_flow_of_the_shipped_build_contour_on_the_cap_it_reaches_first_the_review_to_fix_visit_cap()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = NeverClean();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Never clean");

        FlowView paused = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        paused.PauseReason.Should().Be("Step 'adversarial-review' reached the visit cap of 3 on its transition to 'fix'.");
        paused.Lever.Should().NotBeNullOrWhiteSpace();
        paused.Spawns.Should().BeLessThan(ShippedContours.Build.SpawnCap, "the visit cap binds before the spawn cap");
        paused.Cost.Should().BeLessThan(ShippedContours.Build.Budget);
        executor.Spawns.Should().HaveCount(paused.Spawns);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_project_copy_of_the_build_contour_whose_spawn_cap_binds_first_pauses_on_the_same_script_naming_the_spawn_cap()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = NeverClean();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Contour copy = await Publishing.ContourAsync(daemon, Project, ShippedContours.Build with { SpawnCap = 4 });
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Never clean, fewer spawns");

        FlowView paused = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        paused.ContourHash.Should().Be(copy.Hash, "the project's copy is what resolution returns for the binding");
        paused.PauseReason.Should().Be("Contour 'build' reached its spawn cap of 4; going to judgment step 'fix' would spawn another agent.");
        paused.Lever.Should().NotBeNullOrWhiteSpace();
        paused.Steps.Count(s => s.Step == "adversarial-review").Should().BeLessThan(4, "the visit cap of 3 was never reached");
        executor.Spawns.Should().HaveCount(4);

        await daemon.StopAsync(None);
    }

    // 4. The depth cap on composition.

    [Fact]
    public async Task A_contour_following_its_own_outcome_chains_to_its_depth_cap_and_pauses_the_next_link_before_any_step_even_when_resumed()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Executor(("reviewer-adversarial", [.. Enumerable.Range(1, 5).Select(n => Costing("clean", 0.1m, n))]));
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Contour loop = await Publishing.ContourAsync(daemon, Project, Reviewing("loop", "looped", 5m, new ContourFollow { Outcome = "looped", Contour = "loop", DepthCap = 2 }));
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Round and round", null), None);

        FlowView root = await Flowing.StartAsync(client, Project, parcel.Id, loop.Name);
        FlowView capped = await AtDepthAsync(client, parcel.Id, 3);
        capped = await Flowing.UntilAsync(client, Project, capped.Id, "paused");

        List<FlowView> flows = await Flowing.OfParcelAsync(client, Project, parcel.Id);
        flows.Select(f => f.Depth).Should().Equal(0, 1, 2, 3);
        flows.Take(3).Should().OnlyContain(f => f.Status == "ended" && f.Outcome == "looped");
        flows.Should().OnlyContain(f => f.RootFlowId == root.Id);
        capped.ParentFlowId.Should().Be(flows[2].Id);
        capped.PauseReason.Should().Be(
            $"Flow {Ids.Short(capped.Id)} is at depth 3 of its chain, past the depth cap of 2 on the follow of contour 'loop'; a flow deeper than its follow's depth cap performs no step.");
        capped.Lever.Should().Be(FlowLevers.DepthCapReached("loop"));
        FlowStepView first = capped.Steps.Should().ContainSingle().Subject;
        first.Answer.Should().BeNull();
        first.Result.Should().BeNull();
        executor.Spawns.Should().HaveCount(3, "only the flows at depths 0, 1, and 2 spawned");

        FlowView resumed = await Flowing.ResumeAsync(client, Project, capped.Id, "Go on anyway.");
        FlowView again = await PausedAfterAsync(client, capped.Id, resumed.UpdatedAt);

        again.PauseReason.Should().Be(capped.PauseReason, "a resume is no override of the depth cap");
        again.Steps.Should().ContainSingle().Which.Answer.Should().BeNull();
        executor.Spawns.Should().HaveCount(3, "the depth-capped flow never spawns");
        (await Flowing.OfParcelAsync(client, Project, parcel.Id)).Should().HaveCount(4, "no flow starts at depth 4");

        await daemon.StopAsync(None);
    }

    // 5. The per-flow budget and the budget a chain shares.

    [Fact]
    public async Task A_build_flow_whose_first_judgment_costs_its_whole_budget_pauses_at_its_next_judgment_without_spawning()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Executor(
            ("build", [Costing("done", ShippedContours.Build.Budget, 1)]),
            ("reviewer-adversarial", [Costing("clean", 0.1m, 2)]),
            ("reviewer-conformance", [Costing("clean", 0.1m, 3)]));
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Spent at once");

        FlowView paused = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        paused.CurrentStep.Should().Be("adversarial-review");
        paused.PauseReason.Should().Be(
            $"Flow {Ids.Short(paused.Id)} has recorded a cost of $25.00, which reaches the budget of $25.00 of contour 'build'; a flow performs no judgment once its recorded cost reaches its contour's budget.");
        paused.Lever.Should().Be(FlowLevers.BudgetReached());
        paused.Steps[^1].Answer.Should().BeNull();
        executor.Spawns.Select(s => s.Agent).Should().Equal("build");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_chain_whose_flows_stay_under_their_own_budgets_pauses_at_its_next_judgment_once_together_they_reach_the_roots_budget()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Executor(("reviewer-adversarial", [.. Enumerable.Range(1, 4).Select(n => Costing("clean", 0.6m, n))]));
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Contour seed = await Publishing.ContourAsync(daemon, Project, Reviewing("seed", "grown", 1m, follow: null));
        await Publishing.ContourAsync(daemon, Project, Reviewing("grow", "grown", 5m, new ContourFollow { Outcome = "grown", DepthCap = 5 }));
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Shared purse", null), None);

        FlowView root = await Flowing.StartAsync(client, Project, parcel.Id, seed.Name);
        FlowView paused = await Flowing.UntilAsync(client, Project, (await AtDepthAsync(client, parcel.Id, 2)).Id, "paused");

        List<FlowView> flows = await Flowing.OfParcelAsync(client, Project, parcel.Id);
        flows.Select(f => (f.ContourName, f.Status)).Should().Equal(("seed", "ended"), ("grow", "ended"), ("grow", "paused"));
        flows.Should().OnlyContain(f => f.Cost < 1m, "each flow stays under its own budget");
        paused.PauseReason.Should().Be(
            $"The chain of flow {Ids.Short(paused.Id)}, rooted at flow {Ids.Short(root.Id)}, has recorded a cost of $1.20 across its flows, which reaches the budget of $1.00 of contour 'seed' that its root flow runs; a chain performs no judgment once its recorded cost reaches that budget.");
        paused.Lever.Should().Be(FlowLevers.BudgetReached());
        executor.Spawns.Should().HaveCount(2);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_judgment_that_pauses_after_its_retry_records_the_summed_usage_and_a_resume_pauses_on_the_budget_without_spawning()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Executor(("build",
        [
            new ScriptEntry(null, "/transcripts/1.jsonl", new Measurement(400, 10m, 2, TimeSpan.FromSeconds(20))),
            new ScriptEntry("not json", "/transcripts/2.jsonl", new Measurement(600, 15m, 3, TimeSpan.FromSeconds(40))),
            Costing("done", 0.1m, 3),
        ]));
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Never answers");

        FlowView paused = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        paused.CurrentStep.Should().Be("build");
        paused.PauseReason.Should().StartWith("Agent 'build' gave no valid 'build' answer after one retry");
        paused.PauseUsage.Should().Be(new Measurement(1000, 25m, 5, TimeSpan.FromSeconds(60)), "the pause records both attempts' usage");
        paused.Cost.Should().Be(25m);

        FlowView resumed = await Flowing.ResumeAsync(client, Project, paused.Id, "Try once more.");
        FlowView again = await PausedAfterAsync(client, paused.Id, resumed.UpdatedAt);

        again.PauseReason.Should().Be(
            $"Flow {Ids.Short(paused.Id)} has recorded a cost of $25.00, which reaches the budget of $25.00 of contour 'build'; a flow performs no judgment once its recorded cost reaches its contour's budget.");
        again.Steps.Count(s => s.Step == "build").Should().Be(1);
        executor.Spawns.Should().ContainSingle("a resume of a spent flow spawns nothing");

        await daemon.StopAsync(None);
    }

    // 6. Refused at load without the caps.

    public static TheoryData<string, string, string> Uncapped => new()
    {
        { "the budget removed", Without(ShippedContours.Build, contour => contour.Remove("Budget")), "Contour 'build' needs a budget greater than zero; every contour declares one." },
        { "the spawn cap removed", Without(ShippedContours.Build, contour => contour.Remove("SpawnCap")), "Contour 'build' needs a spawn cap greater than zero; every contour declares one." },
        {
            "one transition's visit cap removed",
            Without(ShippedContours.Build, contour => Transition(contour, "adversarial-review", "not-clean").Remove("VisitCap")),
            "The transition of step 'adversarial-review' in contour 'build' on 'verdict' needs a visit cap greater than zero; every transition declares one."
        },
        {
            "a follow with no depth cap",
            Without(ShippedContours.Build with { Name = "after-build", Follow = new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = 1 } }, contour => contour["Follow"]?.AsObject().Remove("DepthCap")),
            "Contour 'after-build' follows an outcome, so its follow needs a depth cap greater than zero; every composition declares one."
        },
    };

    [Theory]
    [MemberData(nameof(Uncapped))]
    public async Task A_contour_drafted_without_a_cap_is_refused_quoting_the_rule_and_appends_no_event(string missing, string content, string rule)
    {
        missing.Should().NotBeNullOrEmpty();
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All);
        var definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);
        Contour read = Contour.FromJson(content);
        long before = await store.Database.FetchHighestEventSequenceNumber(None);

        await FluentActions.Awaiting(() => definitions.DraftAsync(
                Principal.LocalOwner, DefinitionKind.Contour, DefinitionScope.Project, Project, read.Name, content, read.Hash, null, None))
            .Should().ThrowAsync<DomainException>().WithMessage(rule);

        (await store.Database.FetchHighestEventSequenceNumber(None)).Should().Be(before, "a refused draft records nothing");
    }

    /// <summary>The contour's canonical content with something taken out of it, as a person editing the file might leave it.</summary>
    private static string Without(Contour contour, Action<JsonObject> remove)
    {
        JsonObject content = JsonNode.Parse(Contour.CanonicalJson(contour))?.AsObject()
            ?? throw new InvalidOperationException("A contour's canonical content is a JSON object.");
        remove(content);
        return content.ToJsonString();
    }

    private static JsonObject Transition(JsonObject contour, string step, string verdict) =>
        contour["Steps"]?.AsArray().Single(s => (string?)s?["Name"] == step)?["Transitions"]?.AsArray()
            .Single(t => (string?)t?["Value"] == verdict)?.AsObject()
        ?? throw new InvalidOperationException($"The contour has no transition of '{step}' on '{verdict}'.");

    /// <summary>A contour of one adversarial review that ends on either verdict.</summary>
    private static Contour Reviewing(string name, string outcome, decimal budget, ContourFollow? follow) => new()
    {
        Name = name,
        Accepts = ["task"],
        Outcome = outcome,
        Budget = budget,
        SpawnCap = 1,
        Follow = follow,
        Steps =
        [
            new ContourStep
            {
                Name = "review",
                Agent = "reviewer-adversarial",
                AnswerSchema = "review",
                Transitions =
                [
                    new ContourTransition { Field = "verdict", Value = ContourValue.Of("clean"), VisitCap = 1, Target = Contour.End },
                    new ContourTransition { Field = "verdict", Value = ContourValue.Of("not-clean"), VisitCap = 1, Target = Contour.End },
                ],
            },
        ],
    };

    private static async Task<FlowView> AtDepthAsync(DaemonClient client, Guid parcelId, int depth)
    {
        FlowView? found = null;
        await Flowing.UntilAsync(
            async () =>
            {
                found = (await Flowing.OfParcelAsync(client, Project, parcelId)).SingleOrDefault(f => f.Depth == depth);
                return found is not null;
            },
            $"a flow at depth {depth} starts on parcel {parcelId}");
        return found ?? throw new InvalidOperationException("The wait ended with no flow at that depth.");
    }

    /// <summary>Waits for the flow to pause again after a resume recorded at <paramref name="resumedAt"/>.</summary>
    private static async Task<FlowView> PausedAfterAsync(DaemonClient client, Guid flowId, DateTimeOffset resumedAt)
    {
        FlowView? found = null;
        await Flowing.UntilAsync(
            async () =>
            {
                found = await Flowing.ShowAsync(client, Project, flowId);
                return found is { Status: "paused" } && found.UpdatedAt > resumedAt;
            },
            $"flow {flowId} pauses again after its resume");
        return found ?? throw new InvalidOperationException("The wait ended with no flow.");
    }
}
