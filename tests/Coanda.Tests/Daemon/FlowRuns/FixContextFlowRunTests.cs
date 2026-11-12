using System.Globalization;
using Coanda.Cli;
using Coanda.Modules.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Sdk.Commands;
using Coanda.Daemon.Executors;
using Coanda.Tests.Daemon.Commands;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// The shipped build flow's fix step is spawned with the latest gates result and review findings
/// recorded on its flow run, so it acts on what routed it there, and with nothing older.
/// </summary>
public sealed class FixContextFlowRunTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static ScriptEntry Review(string verdict, string finding, int ordinal) => new(
        $$"""{"verdict":"{{verdict}}","findings":[{"title":"{{finding}}"}]}""",
        $"/transcripts/review-{ordinal}.jsonl",
        Scripts.Usage(ordinal));

    /// <summary>The line the fix step's context holds for <paramref name="step"/>'s visit numbered <paramref name="visit"/> on <paramref name="flowRun"/>.</summary>
    private static string SourceLine(FlowRunView flowRun, string step, int visit)
    {
        FlowRunStepView recorded = flowRun.Steps.Where(s => s.Step == step).ElementAt(visit - 1);
        string document = recorded.Answer?.GetRawText() ?? recorded.Result?.GetRawText() ?? "missing";
        string at = recorded.RecordedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "missing";
        return $"Source {step}: visit {visit}, recorded {at}: {document}";
    }

    [Fact]
    public async Task Each_fix_spawn_carries_the_latest_gates_result_and_review_findings_and_nothing_older()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1)],
            ["reviewer-adversarial"] =
            [
                Review("not-clean", "The first finding.", 2),
                Review("not-clean", "The second finding.", 4),
                Review("clean", "Nothing left.", 6),
            ],
            ["fix"] = [Scripts.Answer("done", 3), Scripts.Answer("done", 5)],
            ["reviewer-conformance"] = [Review("clean", "Conforms.", 7)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Fix what the reviews found");

        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        Assert.Equal(["worktree", "build", "gates", "review-diff", "adversarial-review", "fix", "gates", "review-diff", "adversarial-review", "fix", "gates", "review-diff", "adversarial-review", "conformance-review", "open-pr", "move-to-review"], flowRun.Steps.Select(s => s.Step));
        string[][] fixes = [.. executor.Spawns.Where(s => s.Agent == "fix").Select(s => s.Context.Split('\n'))];
        Assert.Equal(2, fixes.Length);

        Assert.Equal([SourceLine(flowRun, "gates", 1), SourceLine(flowRun, "adversarial-review", 1), "Source conformance-review: has not run on this flow run yet"], fixes[0][^3..]);
        Assert.Contains("""{"verdict":"done",""", fixes[0][^3], StringComparison.Ordinal);
        Assert.EndsWith("""{"verdict":"not-clean","findings":[{"title":"The first finding."}]}""", fixes[0][^2], StringComparison.Ordinal);

        Assert.Equal([SourceLine(flowRun, "gates", 2), SourceLine(flowRun, "adversarial-review", 2), "Source conformance-review: has not run on this flow run yet"], fixes[1][^3..]);
        Assert.EndsWith("""{"verdict":"not-clean","findings":[{"title":"The second finding."}]}""", fixes[1][^2], StringComparison.Ordinal);
        string second = string.Join('\n', fixes[1]);
        Assert.DoesNotContain("visit 1,", second, StringComparison.Ordinal);
        Assert.DoesNotContain("The first finding.", second, StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_fix_spawned_after_failing_gates_carries_the_failed_result_with_its_reason_and_no_review()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        int tests = 0;
        runner.On(
            c => c.IsShell(SoftwareSettings.DefaultTest),
            (_, _) => Task.FromResult(Interlocked.Increment(ref tests) == 1
                ? new CommandResult(1, "Failed ParserTests.ReadsEmpty\n", string.Empty, "Failed ParserTests.ReadsEmpty\n")
                : ScriptedCommandRunner.Ok()));
        Dictionary<string, IReadOnlyList<ScriptEntry>> script = Scripts.CleanBuild();
        script["fix"] = [Scripts.Answer("done", 4)];
        var executor = new ScriptedExecutor(script);
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = executor, CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Fix what the tests found");

        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        Assert.Equal(["worktree", "build", "gates", "fix", "gates"], flowRun.Steps.Select(s => s.Step).Take(5));
        string[] fix = executor.Spawns.Single(s => s.Agent == "fix").Context.Split('\n');
        Assert.Equal([SourceLine(flowRun, "gates", 1), "Source adversarial-review: has not run on this flow run yet", "Source conformance-review: has not run on this flow run yet"], fix[^3..]);
        Assert.Contains("\"verdict\":\"failed\"", fix[^3], StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"The test command 'dotnet test' exited with code 1. The last 1 lines of its output:\\nFailed ParserTests.ReadsEmpty\"", fix[^3], StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }
}
