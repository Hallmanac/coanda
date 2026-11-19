using Coanda.Core;
using Coanda.Modules.Software.Repositories;
using Coanda.Sdk.Commands;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Modules.Software.Repositories;

/// <summary>
/// The software module's start sweep against the scripted command runner: it repairs every linked
/// worktree of every clone under the node home whose directory exists, all of a clone's repairs before
/// it looks inside or removes any of that clone's worktrees, and logs and passes over what it cannot do.
/// </summary>
public sealed class ProjectCloneSweepTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryHome temp = new();
    private readonly ScriptedCommandRunner runner = new();
    private readonly RecordingLogger log = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task The_sweep_repairs_every_linked_worktree_whose_directory_exists_in_every_clone_and_none_whose_directory_is_gone()
    {
        string alpha = MakeClone("alpha", "repo");
        string alphaTwo = MakeClone("alpha", "two");
        string beta = MakeClone("beta", "repo");
        string partial = MakeClone("alpha", ".repo.partial");
        string paused = WorktreeDirectory("alpha", Ids.New());
        string byHand = Directory.CreateDirectory(Path.Combine(temp.Home.Path, "someones-own")).FullName;
        string gone = temp.Home.FlowRunWorkPath("alpha", Ids.New());
        string other = WorktreeDirectory("beta", Ids.New());
        string inPartial = WorktreeDirectory("alpha", Ids.New());
        runner.AddWorktree(alpha, paused, "coanda/aaaa1111", ScriptedCommandRunner.PrimaryHead);
        runner.AddWorktree(alpha, byHand, "mine", ScriptedCommandRunner.PrimaryHead);
        runner.AddWorktree(alpha, gone, "coanda/bbbb2222", ScriptedCommandRunner.PrimaryHead, "prunable gitdir file points to non-existent location");
        runner.AddWorktree(beta, other, "coanda/cccc3333", ScriptedCommandRunner.PrimaryHead);
        runner.AddWorktree(partial, inPartial, "coanda/dddd4444", ScriptedCommandRunner.PrimaryHead);

        await SweepAsync(NoFlowRunEnded);

        Assert.Equal(
            [
                $"--git-dir={alpha} worktree list --porcelain",
                $"--git-dir={alpha} worktree repair {paused}",
                $"--git-dir={alpha} worktree repair {byHand}",
                $"--git-dir={alphaTwo} worktree list --porcelain",
                $"--git-dir={beta} worktree list --porcelain",
                $"--git-dir={beta} worktree repair {other}",
            ],
            runner.Commands.Where(c => c.IsGit("worktree")).Select(c => string.Join(' ', c.Arguments)));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Every_repair_of_a_clone_runs_before_any_status_or_removal_in_its_worktrees()
    {
        string clone = MakeClone("alpha", "repo");
        Guid endedId = Ids.New();
        string ended = WorktreeDirectory("alpha", endedId);
        string paused = WorktreeDirectory("alpha", Ids.New());
        runner.AddWorktree(clone, ended, "coanda/aaaa1111", ScriptedCommandRunner.PrimaryHead);
        runner.AddWorktree(clone, paused, "coanda/bbbb2222", ScriptedCommandRunner.PrimaryHead);

        await SweepAsync((_, flowRunId, _) => Task.FromResult<string?>(flowRunId == endedId ? ended : null));

        IReadOnlyList<string> calls = [.. runner.Commands.Where(c => c.Program == "git").Select(Describe)];
        Assert.Equal(
            [
                $"{clone}: worktree list --porcelain",
                $"{clone}: worktree repair {ended}",
                $"{clone}: worktree repair {paused}",
                $"{ended}: status --porcelain --untracked-files=all",
                $"{clone}: worktree remove --force --force {ended}",
            ],
            calls.Where(c => c.Contains(": worktree ", StringComparison.Ordinal) || c.Contains(": status ", StringComparison.Ordinal)));
        Assert.Equal([paused], runner.Worktrees(clone));
    }

    [Fact]
    public async Task A_listing_that_fails_is_logged_naming_its_clone_and_the_sweep_goes_on_to_the_next_clone()
    {
        string alpha = MakeClone("alpha", "repo");
        string beta = MakeClone("beta", "repo");
        string other = WorktreeDirectory("beta", Ids.New());
        runner.AddWorktree(beta, other, "coanda/aaaa1111", ScriptedCommandRunner.PrimaryHead);
        runner.On(c => c.IsGit("worktree", "list") && c.GitDirectory() == alpha, ScriptedCommandRunner.Fails(128, "fatal: unable to read worktrees"));

        await SweepAsync(NoFlowRunEnded);

        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Error
                && e.Message.Contains($"The worktrees of the clone at {alpha} could not be listed", StringComparison.Ordinal)
                && e.Message.Contains("the next start tries again", StringComparison.Ordinal)
                && e.Message.Contains("unable to read worktrees", StringComparison.Ordinal));
        Assert.Contains($"worktree repair {other}", runner.GitCalls);
    }

    [Fact]
    public async Task A_repair_that_fails_is_logged_naming_its_worktree_and_clone_and_the_sweep_goes_on_to_the_next_worktree_and_its_removals()
    {
        string clone = MakeClone("alpha", "repo");
        string first = WorktreeDirectory("alpha", Ids.New());
        Guid endedId = Ids.New();
        string ended = WorktreeDirectory("alpha", endedId);
        runner.AddWorktree(clone, first, "coanda/aaaa1111", ScriptedCommandRunner.PrimaryHead);
        runner.AddWorktree(clone, ended, "coanda/bbbb2222", ScriptedCommandRunner.PrimaryHead);
        runner.On(c => c.IsGit("worktree", "repair") && c.Arguments[^1] == first, ScriptedCommandRunner.Fails(128, "fatal: not a valid path"));

        await SweepAsync((_, flowRunId, _) => Task.FromResult<string?>(flowRunId == endedId ? ended : null));

        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Error
                && e.Message.Contains($"The worktree at {first} could not be repaired from its clone at {clone}", StringComparison.Ordinal)
                && e.Message.Contains("the next start tries again", StringComparison.Ordinal)
                && e.Message.Contains("not a valid path", StringComparison.Ordinal));
        Assert.Contains($"worktree repair {ended}", runner.GitCalls);
        Assert.Equal([first], runner.Worktrees(clone));
    }

    private static Task<string?> NoFlowRunEnded(string project, Guid flowRunId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    private static string Describe(CommandLine command) =>
        $"{command.GitDirectory() ?? command.WorkingDirectory}: {string.Join(' ', command.GitArguments())}";

    private string MakeClone(string project, string repo) =>
        Directory.CreateDirectory(Path.Combine(temp.Home.Path, "projects", project, "repos", repo)).FullName;

    private string WorktreeDirectory(string project, Guid flowRunId) =>
        Directory.CreateDirectory(temp.Home.FlowRunWorkPath(project, flowRunId)).FullName;

    private async Task SweepAsync(Func<string, Guid, CancellationToken, Task<string?>> finalWorkPath)
    {
        using ILoggerFactory factory = LoggerFactory.Create(b => b.AddProvider(log));
        var clones = new ProjectClones(temp.Home.Path, runner, factory.CreateLogger<ProjectClones>());
        await clones.SweepAsync(finalWorkPath, None);
    }
}
