using Coanda.Core.Flights;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.Flights;

/// <summary>Canned answers for the shipped build flow's agents.</summary>
public static class Scripts
{
    public static ScriptEntry Answer(string verdict, int ordinal) => new(
        $$"""{"verdict":"{{verdict}}","summary":"answer {{ordinal}}"}""",
        $"/transcripts/{ordinal}.jsonl",
        Usage(ordinal));

    public static Measurement Usage(int ordinal) =>
        new(1000L * ordinal, 0.25m * ordinal, ordinal, TimeSpan.FromSeconds(30 * ordinal));

    /// <summary>build answers done and both reviewers answer clean: the build flow's clean path.</summary>
    public static Dictionary<string, IReadOnlyList<ScriptEntry>> CleanBuild() => new()
    {
        ["build"] = [Answer("done", 1)],
        ["reviewer-adversarial"] = [Answer("clean", 2)],
        ["reviewer-conformance"] = [Answer("clean", 3)],
    };

    public static ScriptedExecutor CleanBuildExecutor() => new(CleanBuild());
}
