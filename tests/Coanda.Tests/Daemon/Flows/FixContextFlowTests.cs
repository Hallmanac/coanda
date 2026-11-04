using System.Globalization;
using Coanda.Cli;
using Coanda.Core.Flows;
using ProjectDefaults = Coanda.Core.Projects.Project;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Commands;
using Coanda.Daemon.Executors;
using Coanda.Tests.Daemon.Commands;

namespace Coanda.Tests.Daemon.Flows;

/// <summary>
/// The shipped build contour's fix step is spawned with the latest gates result and review findings
/// recorded on its flow, so it acts on what routed it there, and with nothing older: nothing an
/// earlier fix visit already acted on.
/// </summary>
public sealed class FixContextFlowTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static ScriptEntry Review(string verdict, string finding, int ordinal) => new(
        $$"""{"verdict":"{{verdict}}","findings":[{"title":"{{finding}}"}]}""",
        $"/transcripts/review-{ordinal}.jsonl",
        Scripts.Usage(ordinal));

    /// <summary>The line the fix step's context holds for <paramref name="step"/>'s visit numbered <paramref name="visit"/> on <paramref name="flow"/>.</summary>
    private static string SourceLine(FlowView flow, string step, int visit)
    {
        FlowStepView recorded = flow.Steps.Where(s => s.Step == step).ElementAt(visit - 1);
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
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Fix what the reviews found");

        FlowView flow = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        flow.Steps.Select(s => s.Step).Should().Equal(
            "worktree", "build", "gates", "adversarial-review", "fix", "gates", "adversarial-review", "fix",
            "gates", "adversarial-review", "conformance-review", "open-pr", "gated-merge", "move-to-review");
        string[][] fixes = [.. executor.Spawns.Where(s => s.Agent == "fix").Select(s => s.Context.Split('\n'))];
        fixes.Should().HaveCount(2);

        fixes[0][^3..].Should().Equal(
            SourceLine(flow, "gates", 1),
            SourceLine(flow, "adversarial-review", 1),
            "Source conformance-review: has not run on this flow yet");
        fixes[0][^3].Should().Contain("""{"verdict":"done",""");
        fixes[0][^2].Should().EndWith("""{"verdict":"not-clean","findings":[{"title":"The first finding."}]}""");

        fixes[1][^3..].Should().Equal(
            SourceLine(flow, "gates", 2),
            SourceLine(flow, "adversarial-review", 2),
            "Source conformance-review: has not run on this flow yet");
        fixes[1][^2].Should().EndWith("""{"verdict":"not-clean","findings":[{"title":"The second finding."}]}""");
        string second = string.Join('\n', fixes[1]);
        second.Should().NotContain("visit 1,").And.NotContain("The first finding.");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_fix_spawned_after_failing_gates_carries_the_failed_result_with_its_reason_and_no_review()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        int tests = 0;
        runner.On(
            c => c.IsShell(ProjectDefaults.DefaultTest),
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
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Fix what the tests found");

        FlowView flow = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        flow.Steps.Select(s => s.Step).Take(5).Should().Equal("worktree", "build", "gates", "fix", "gates");
        string[] fix = executor.Spawns.Single(s => s.Agent == "fix").Context.Split('\n');
        fix[^3..].Should().Equal(
            SourceLine(flow, "gates", 1),
            "Source adversarial-review: has not run on this flow yet",
            "Source conformance-review: has not run on this flow yet");
        fix[^3].Should().Contain("\"verdict\":\"failed\"").And.Contain(
            "\"reason\":\"The test command 'dotnet test' exited with code 1. The last 1 lines of its output:\\nFailed ParserTests.ReadsEmpty\"");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_fix_sent_back_by_failing_gates_is_not_handed_the_review_findings_the_previous_fix_acted_on()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        int tests = 0;
        runner.On(
            c => c.IsShell(ProjectDefaults.DefaultTest),
            (_, _) => Task.FromResult(Interlocked.Increment(ref tests) == 2
                ? new CommandResult(1, "Failed ParserTests.ReadsEmpty\n", string.Empty, "Failed ParserTests.ReadsEmpty\n")
                : ScriptedCommandRunner.Ok()));
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1)],
            ["reviewer-adversarial"] = [Review("not-clean", "The acted-on finding.", 2), Review("clean", "Nothing left.", 5)],
            ["fix"] = [Scripts.Answer("done", 3), Scripts.Answer("done", 4)],
            ["reviewer-conformance"] = [Review("clean", "Conforms.", 6)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = executor, CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Fix what broke after the review");

        FlowView flow = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        flow.Steps.Select(s => s.Step).Take(7).Should().Equal(
            "worktree", "build", "gates", "adversarial-review", "fix", "gates", "fix");
        string[] second = [.. executor.Spawns.Where(s => s.Agent == "fix").Select(s => s.Context).ElementAt(1).Split('\n')];
        second[^3..].Should().Equal(
            SourceLine(flow, "gates", 2),
            "Source adversarial-review: has recorded nothing since fix visit 1",
            "Source conformance-review: has not run on this flow yet");
        second[^3].Should().Contain("\"verdict\":\"failed\"");
        string.Join('\n', second).Should().NotContain("The acted-on finding.");

        await daemon.StopAsync(None);
    }
}
