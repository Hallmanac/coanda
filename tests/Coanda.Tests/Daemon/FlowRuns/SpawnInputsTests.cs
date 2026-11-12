using Coanda.Cli;
using Coanda.Core.Agents;
using Coanda.Core.Flows;
using Coanda.Core.FlowRuns;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>What the runner hands each spawn: its cost ceiling, and the files it writes into the work directory first.</summary>
public sealed class SpawnInputsTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_second_agent_step_in_a_flow_run_that_recorded_a_cost_carries_the_budget_less_that_cost_as_its_ceiling()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Scripts.CleanBuildExecutor();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Spend some");

        await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        decimal budget = SoftwareFlows.Build.Budget;
        Assert.Equal([budget, budget - Scripts.Usage(1).CostUsd, budget - Scripts.Usage(1).CostUsd - Scripts.Usage(2).CostUsd], executor.Spawns.Select(s => s.Caps.CostCeilingUsd));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_step_in_a_chained_flow_run_carries_the_chains_remainder_when_it_is_the_smaller()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["reviewer-adversarial"] = [Costing("clean", 0.6m, 1), Costing("clean", 0.1m, 2)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow seed = await Publishing.FlowAsync(daemon, Project, Reviewing("seed", "grown", 1m, follow: null));
        await Publishing.FlowAsync(daemon, Project, Reviewing("grow", "grown-again", 5m, new FlowFollow { Outcome = "grown", DepthCap = 1 }));
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Shared purse", null), None);

        await FlowRunDriver.StartAsync(client, Project, parcel.Id, seed.Name);
        await FlowRunDriver.UntilAsync(
            async () => (await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id)).Count(f => f.Status == "ended") == 2,
            "the seed flow run and the flow run following it both end");

        // the follower's own budget leaves $5.00, and its chain, rooted at a $1.00 flow that already spent $0.60, leaves $0.40
        Assert.Equal([1m, 0.4m], executor.Spawns.Select(s => s.Caps.CostCeilingUsd));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Each_spawn_finds_the_parcel_record_and_its_context_under_coanda_in_the_work_directory_written_fresh_from_the_parcel()
    {
        using var temp = new TemporaryHome();
        var executor = new TamperingExecutor(Scripts.CleanBuildExecutor());
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Render the record", pullRequest: true);

        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        string work = temp.Home.FlowRunWorkPath(Project, flowRun.Id);
        Assert.Equal(3, executor.Seen.Count);
        foreach ((string task, string prompt, ScriptedSpawn spawn) in executor.Seen.Zip(executor.Inner.Spawns, (seen, spawn) => (seen.Task, seen.Prompt, spawn)))
        {
            Assert.Equal(work, spawn.WorkingDirectory);
            // prompt.md holds the context the spawn carries
            Assert.Equal(spawn.Context, prompt);
            // the session rewrote it, and the next spawn wrote it again from the parcel
            Assert.DoesNotContain(TamperingExecutor.Tampered, task, StringComparison.Ordinal);
            Assert.Equal($"""
                # Render the record

                - Parcel: {parcel.Id}
                - Type: task
                - Stage: build
                - Title: Render the record

                ## objective

                {TaskFields.ObjectiveValue.Text}

                ## criteria

                - A type declares fields.
                - A gate reads them.

                ## non-goals

                not set

                ## pull-request

                42 (https://github.com/example/coanda/pull/42)

                ## repos

                - repo

                """.ReplaceLineEndings("\n"), task);
        }

        // the daemon never reads the file back
        Assert.Equal(TamperingExecutor.Tampered, File.ReadAllText(Path.Combine(work, ".coanda", "task.md")));
        Assert.Equivalent(
            new[] { ".coanda", ".coanda/task.md", ".coanda/prompt.md" },
            Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(work, p).Replace('\\', '/')),
            strict: true);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(3, executor.Modes.Count);
            Assert.All(executor.Modes, modes => Assert.True(modes.Task == OwnerOnly && modes.Prompt == OwnerOnly, "each spawn finds both written owner-only, the record again after the session left it wider"));
        }

        await daemon.StopAsync(None);
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static ScriptEntry Costing(string verdict, decimal cost, int ordinal) => new(
        $$"""{"verdict":"{{verdict}}","findings":[]}""",
        $"/transcripts/{ordinal}.jsonl",
        new Measurement(100 * ordinal, cost, 1, TimeSpan.FromSeconds(ordinal)));

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

    /// <summary>
    /// A scripted executor whose sessions read the two files at their spawn and then rewrite the
    /// parcel record, widening its mode, the way a session with an edit tool could.
    /// </summary>
    private sealed class TamperingExecutor(ScriptedExecutor inner) : IAgentExecutor
    {
        public const string Tampered = "tampered by the session";

        private readonly List<(string Task, string Prompt)> seen = [];
        private readonly List<(UnixFileMode Task, UnixFileMode Prompt)> modes = [];

        public ScriptedExecutor Inner => inner;

        public IReadOnlyList<(string Task, string Prompt)> Seen
        {
            get
            {
                lock (seen)
                {
                    return [.. seen];
                }
            }
        }

        /// <summary>The mode of each file as each spawn found it, on macOS and Linux.</summary>
        public IReadOnlyList<(UnixFileMode Task, UnixFileMode Prompt)> Modes
        {
            get
            {
                lock (seen)
                {
                    return [.. modes];
                }
            }
        }

        public async ValueTask<SessionHandle> SpawnAsync(
            AgentDefinition agent, string context, string workingDirectory, SpawnCaps caps, AnswerSchema answerSchema, CredentialProfile? credentialProfile, SessionAccess? access, CancellationToken cancellationToken)
        {
            string task = Path.Combine(workingDirectory, ".coanda", "task.md");
            string prompt = Path.Combine(workingDirectory, ".coanda", "prompt.md");
            lock (seen)
            {
                seen.Add((File.ReadAllText(task), File.ReadAllText(prompt)));
                if (!OperatingSystem.IsWindows())
                {
                    modes.Add((File.GetUnixFileMode(task), File.GetUnixFileMode(prompt)));
                }
            }

            File.WriteAllText(task, Tampered);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(task, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            return await inner.SpawnAsync(agent, context, workingDirectory, caps, answerSchema, credentialProfile, access, cancellationToken);
        }

        public ValueTask<CollectedAnswer> CollectAsync(SessionHandle handle, CancellationToken cancellationToken) => inner.CollectAsync(handle, cancellationToken);

        public ValueTask ResumeAsync(SessionHandle handle, string message, CancellationToken cancellationToken) => inner.ResumeAsync(handle, message, cancellationToken);

        public ValueTask SendAsync(SessionHandle handle, string message, CancellationToken cancellationToken) => inner.SendAsync(handle, message, cancellationToken);

        public ValueTask<StopReport?> StopAsync(SessionHandle handle, CancellationToken cancellationToken) => inner.StopAsync(handle, cancellationToken);

        public ValueTask<IReadOnlyList<SessionHandle>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

        public ValueTask<Reattachment?> ReattachAsync(SessionHandle handle, SessionAccess? access, CancellationToken cancellationToken) => inner.ReattachAsync(handle, access, cancellationToken);
    }
}
