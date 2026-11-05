using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Projects;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;
using Coanda.Tests.Daemon.Commands;
using Fisher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectRecord = Coanda.Core.Projects.Project;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The core's clones: the start moves each clone an earlier build kept under <c>clones/</c> to the
/// per-repo layout with its worktrees, and makes the clone of every repo that has none, as a registration
/// does, all against the scripted command runner's model of git.
/// </summary>
public sealed class RepoClonesTests : IDisposable
{
    private const string Project = "alpha";
    private const string Repo = RegisteredProjects.Repo;
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryHome temp = new();
    private readonly ScriptedCommandRunner runner = new();
    private readonly RecordingLogger log = new();

    private NodeHome Home => temp.Home;

    private string Earlier => Path.Combine(Home.EarlierClonesPath, Project, RepoClones.EarlierCloneName);

    private string Clone => Home.RepoClonePath(Project, Repo);

    private string PartialPath => Path.Combine(Home.Path, "projects", Project, "repos", $".{Repo}.partial");

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task An_earlier_clone_moves_to_the_repos_path_with_its_contents_and_its_worktrees_listed_before_the_move_and_repaired_after()
    {
        await RegisterAsync(Project);
        WriteEarlierClone(Earlier);
        string paused = Directory.CreateDirectory(Home.FlowRunWorkPath(Project, Ids.New())).FullName;
        string gone = Home.FlowRunWorkPath(Project, Ids.New());
        string other = Directory.CreateDirectory(Path.Combine(Home.Path, "someones-own")).FullName;
        CarriedConfiguration();
        bool listedBeforeTheMove = false;
        runner.On(c => c.IsGit("worktree", "list"), (command, _) =>
        {
            if (command.GitDirectory() == Earlier)
            {
                listedBeforeTheMove = Directory.Exists(Earlier) && !Directory.Exists(Clone);
            }

            // the moved clone still lists its worktrees, because their entries moved with it
            return Task.FromResult(ScriptedCommandRunner.Ok(Listing(command.GitDirectory() ?? Earlier, paused, gone, other)));
        });
        List<string> repairedAfterTheMove = [];
        runner.On(c => c.IsGit("worktree", "repair"), (command, _) =>
        {
            if (Directory.Exists(Clone) && !Directory.Exists(Earlier))
            {
                repairedAfterTheMove.Add(command.Arguments[^1]);
            }

            return Task.FromResult(ScriptedCommandRunner.Ok());
        });

        await StartAsync();

        Assert.True(listedBeforeTheMove, "the worktrees are listed while the clone is still at its earlier path");
        Assert.Equal([paused, other], repairedAfterTheMove);
        Assert.Equal(
            [
                $"--git-dir={Earlier} worktree list --porcelain",
                $"--git-dir={Clone} worktree list --porcelain",
                $"--git-dir={Clone} worktree repair {paused}",
                $"--git-dir={Clone} worktree repair {other}",
            ],
            runner.Commands.Where(c => c.IsGit("worktree")).Select(c => string.Join(' ', c.Arguments)));
        // the clone's objects, branches, and branch-owner settings move with it, untouched
        Assert.Equal("ref: refs/heads/main\n", await File.ReadAllTextAsync(Path.Combine(Clone, "HEAD"), None));
        Assert.Equal(EarlierConfig, await File.ReadAllTextAsync(Path.Combine(Clone, "config"), None));
        Assert.Equal("packed", await File.ReadAllTextAsync(Path.Combine(Clone, "objects", "pack", "pack-1.pack"), None));
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, await File.ReadAllTextAsync(Path.Combine(Clone, "refs", "heads", "coanda", "aaaa1111"), None));
        // nothing is left at the earlier path, and clones/ is gone once empty
        Assert.False(Directory.Exists(Home.EarlierClonesPath));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_moved_clone_whose_origin_is_the_recorded_remote_gains_the_ledger_refspec_and_keeps_the_default_one()
    {
        await RegisterAsync(Project);
        WriteEarlierClone(Earlier);
        CarriedConfiguration();

        await StartAsync();

        Assert.Equal($"{RepoClones.DefaultRefspec}\n{RepoClones.LedgerRefspec}", runner.Config(Clone, "remote.origin.fetch"));
        Assert.Equal($"config --add remote.origin.fetch {RepoClones.LedgerRefspec}", Assert.Single(runner.GitCalls, c => c.StartsWith("config --add", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_ledger_refspec_fetches_every_nodes_refs_forced_into_a_namespace_no_other_refspec_or_ref_of_the_node_reaches()
    {
        Assert.Equal("+refs/coanda/nodes/*:refs/coanda/fetched/nodes/*", RepoClones.LedgerRefspec);
        Assert.Equal($"+refs/heads/*:refs/remotes/{Git.Remote}/*", RepoClones.DefaultRefspec);
        foreach (string taken in new[] { "refs/heads/", "refs/tags/", "refs/remotes/", "refs/coanda/nodes/", "refs/coanda/rebase/" })
        {
            Assert.False(RepoClones.LedgerNamespace.StartsWith(taken, StringComparison.Ordinal), taken);
            Assert.False(taken.StartsWith(RepoClones.LedgerNamespace, StringComparison.Ordinal), taken);
        }
    }

    [Fact]
    public async Task A_clone_whose_project_has_no_record_is_left_where_it_is_and_logged()
    {
        WriteEarlierClone(Earlier);

        await StartAsync();

        Assert.True(Directory.Exists(Earlier));
        Assert.Empty(runner.Commands);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains($"no project named '{Project}' is registered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_clone_whose_destination_something_occupies_is_left_where_it_is_and_logged_and_neither_is_touched()
    {
        await RegisterAsync(Project);
        WriteEarlierClone(Earlier);
        Directory.CreateDirectory(Clone);
        await File.WriteAllTextAsync(Path.Combine(Clone, "mine.txt"), "someone's", None);
        runner.On(c => c.IsGit("rev-parse", "--is-bare-repository"), ScriptedCommandRunner.Fails(128, "fatal: not a git repository"));

        await StartAsync();

        Assert.True(Directory.Exists(Earlier));
        Assert.Equal("someone's", await File.ReadAllTextAsync(Path.Combine(Clone, "mine.txt"), None));
        Assert.DoesNotContain(runner.Commands, c => c.IsGit("worktree"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains($"something is already at {Clone}", StringComparison.Ordinal));
        // the start then finds no bare clone at the path, and says so
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("is not a bare clone", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Commands, c => c.IsGit("init") || c.IsGit("config", "--add"));
    }

    [Fact]
    public async Task A_clone_whose_worktrees_cannot_be_listed_is_left_where_it_is_and_logged()
    {
        await RegisterAsync(Project);
        WriteEarlierClone(Earlier);
        runner.On(c => c.IsGit("worktree", "list"), ScriptedCommandRunner.Fails(128, "fatal: unable to read worktrees"));

        await StartAsync();

        Assert.True(Directory.Exists(Earlier));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("its worktrees could not be listed", StringComparison.Ordinal) && e.Message.Contains("unable to read worktrees", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Commands, c => c.IsGit("worktree", "repair"));
        // the clone stays at its earlier path, so the core makes none at the new one either
        Assert.Contains(runner.Commands, c => c.IsGit("init"));
    }

    [Fact]
    public async Task A_clone_whose_project_has_more_than_one_repo_is_left_where_it_is_and_logged()
    {
        ProjectRecord two = await RegisterTwoReposAsync("beta");
        string earlier = Path.Combine(Home.EarlierClonesPath, two.Name, RepoClones.EarlierCloneName);
        WriteEarlierClone(earlier);

        await StartAsync();

        Assert.True(Directory.Exists(earlier));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("project 'beta' has 2 repos", StringComparison.Ordinal));
        // each of its repos gets a clone of its own all the same
        Assert.True(Directory.Exists(Home.RepoClonePath("beta", "one")));
        Assert.True(Directory.Exists(Home.RepoClonePath("beta", "two")));
    }

    [Fact]
    public async Task A_repair_that_fails_is_logged_naming_its_worktree_and_the_clone_stays_moved()
    {
        await RegisterAsync(Project);
        WriteEarlierClone(Earlier);
        string first = Directory.CreateDirectory(Home.FlowRunWorkPath(Project, Ids.New())).FullName;
        string second = Directory.CreateDirectory(Home.FlowRunWorkPath(Project, Ids.New())).FullName;
        ListedAtBothPaths(first, "coanda/aaaa1111");
        ListedAtBothPaths(second, "coanda/bbbb2222");
        runner.On(c => c.IsGit("worktree", "repair") && c.Arguments[^1] == first, ScriptedCommandRunner.Fails(128, "fatal: not a valid path"));

        await StartAsync();

        Assert.True(Directory.Exists(Clone));
        Assert.False(Directory.Exists(Earlier));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains($"The worktree at {first} could not be repaired", StringComparison.Ordinal) && e.Message.Contains("the next start tries again", StringComparison.Ordinal));
        Assert.Contains($"worktree repair {second}", runner.GitCalls);

        // the next start repairs the worktree again, from the clone at its new path
        var again = new ScriptedCommandRunner();
        again.AddWorktree(Clone, first, "coanda/aaaa1111", ScriptedCommandRunner.PrimaryHead);
        again.AddWorktree(Clone, second, "coanda/bbbb2222", ScriptedCommandRunner.PrimaryHead);
        await StartAsync(again);

        Assert.Equal(
            [$"--git-dir={Clone} worktree list --porcelain", $"--git-dir={Clone} worktree repair {first}", $"--git-dir={Clone} worktree repair {second}"],
            again.Commands.Where(c => c.IsGit("worktree")).Select(c => string.Join(' ', c.Arguments)));
    }

    [Fact]
    public async Task A_start_stopped_between_the_move_and_the_repairs_leaves_them_to_the_next_start_which_makes_them()
    {
        await RegisterAsync(Project);
        WriteEarlierClone(Earlier);
        string paused = Directory.CreateDirectory(Home.FlowRunWorkPath(Project, Ids.New())).FullName;
        ListedAtBothPaths(paused, "coanda/aaaa1111");
        using var stopping = new CancellationTokenSource();
        runner.On(c => c.IsGit("rev-parse", "--is-bare-repository"), (_, _) =>
        {
            // the start is stopped once the clone has moved and before its worktrees are repaired
            stopping.Cancel();
            return Task.FromResult(ScriptedCommandRunner.Ok("true\n"));
        });
        runner.On(c => c.IsGit("worktree", "list") && c.GitDirectory() == Clone, (_, token) => Task.FromCanceled<CommandResult>(token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartAsync(cancellationToken: stopping.Token));

        Assert.True(Directory.Exists(Clone));
        Assert.False(Directory.Exists(Earlier));
        Assert.DoesNotContain(runner.Commands, c => c.IsGit("worktree", "repair"));

        var again = new ScriptedCommandRunner();
        again.AddWorktree(Clone, paused, "coanda/aaaa1111", ScriptedCommandRunner.PrimaryHead);
        await StartAsync(again);

        Assert.Contains($"worktree repair {paused}", again.GitCalls);
    }

    [Fact]
    public async Task A_missing_clone_is_made_beside_its_path_from_no_remote_contact_and_moved_there_once_complete()
    {
        await RegisterAsync(Project);
        string partial = PartialPath;
        runner.On(c => c.IsGit("init"), (command, _) =>
        {
            Assert.False(Directory.Exists(Clone), "the clone is made beside its path");
            Directory.CreateDirectory(command.Arguments[^1]);
            return Task.FromResult(ScriptedCommandRunner.Ok());
        });

        await StartAsync();

        Assert.Equal(
            [
                $"init --bare --quiet {partial}",
                $"remote add origin {RegisteredProjects.Remote}",
                $"config --add remote.origin.fetch {RepoClones.LedgerRefspec}",
            ],
            runner.GitCalls);
        Assert.All(runner.Commands.Skip(1), c => Assert.Equal(partial, c.GitDirectory()));
        Assert.True(Directory.Exists(Clone));
        Assert.False(Directory.Exists(partial));
        Assert.Equal(RegisteredProjects.Remote, runner.Config(partial, "remote.origin.url"));
        Assert.Equal($"{RepoClones.DefaultRefspec}\n{RepoClones.LedgerRefspec}", runner.Config(partial, "remote.origin.fetch"));
        Assert.DoesNotContain(runner.Commands, c => c.IsGit("fetch") || c.IsGit("clone") || c.IsGit("ls-remote") || c.IsGit("push"));
    }

    [Fact]
    public async Task A_making_interrupted_part_way_leaves_no_clone_at_the_path_and_the_next_start_makes_it_again()
    {
        await RegisterAsync(Project);
        runner.On(c => c.IsGit("remote", "add"), ScriptedCommandRunner.Fails(128, "error: could not lock config file"));

        await StartAsync();

        Assert.False(Directory.Exists(Clone));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains($"at {Clone} could not be made or checked", StringComparison.Ordinal));

        var again = new ScriptedCommandRunner();
        await StartAsync(again);

        Assert.True(Directory.Exists(Clone));
        Assert.Equal("init", again.Commands[0].Arguments[0]);
    }

    [Fact]
    public async Task A_second_start_changes_nothing_in_an_existing_clone()
    {
        await RegisterAsync(Project);
        await StartAsync();
        // the model keys a clone by its path, so it is told what the made clone carried to its path
        runner.SetConfig(Clone, "remote.origin.url", runner.Config(PartialPath, "remote.origin.url") ?? string.Empty);
        runner.SetConfig(Clone, "remote.origin.fetch", runner.Config(PartialPath, "remote.origin.fetch") ?? string.Empty);
        int before = runner.Commands.Count;

        await StartAsync();

        // listing the clone's worktrees, of which it has none, is the start's repair pass
        Assert.Equal(
            ["rev-parse --is-bare-repository", "worktree list --porcelain", "config --get-all remote.origin.url", "config --get-all remote.origin.fetch"],
            runner.GitCalls.Skip(before));
    }

    [Fact]
    public async Task A_repo_whose_name_is_not_a_plain_directory_name_gets_no_clone_and_is_logged_and_the_project_s_other_repos_get_theirs()
    {
        await RegisterReposAsync(Project, "../escape", "two");

        await StartAsync();

        Assert.True(Directory.Exists(Home.RepoClonePath(Project, "two")));
        Assert.DoesNotContain(runner.Commands, c => c.Arguments.Any(a => a.Contains("escape", StringComparison.Ordinal)));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'../escape'", StringComparison.Ordinal) && e.Message.Contains("not a plain directory name", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task An_existing_clone_whose_origin_is_not_the_recorded_remote_is_left_as_it_is_and_logged()
    {
        await RegisterAsync(Project);
        Directory.CreateDirectory(Clone);
        runner.SetConfig(Clone, "remote.origin.url", "https://github.com/someone/else.git");

        await StartAsync();

        Assert.Equal(["rev-parse --is-bare-repository", "worktree list --porcelain", "config --get-all remote.origin.url"], runner.GitCalls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("https://github.com/someone/else.git", StringComparison.Ordinal) && e.Message.Contains("not", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_start_where_git_cannot_be_run_logs_why_and_goes_on()
    {
        await RegisterAsync(Project);
        await RegisterAsync("beta");

        await StartAsync(new NoCommandRunner());

        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains("could not be made or checked", StringComparison.Ordinal)));
        Assert.False(Directory.Exists(Clone));
    }

    [Fact]
    public async Task The_daemon_moves_and_makes_clones_before_the_software_module_starts_and_a_registration_returns_with_its_clone()
    {
        await RegisterAsync(Project);
        WriteEarlierClone(Earlier);
        string paused = Directory.CreateDirectory(Home.FlowRunWorkPath(Project, Ids.New())).FullName;
        runner.AddWorktree(Earlier, paused, "coanda/aaaa1111", ScriptedCommandRunner.PrimaryHead);
        // the clone at its new path lists the worktree to the core's repair pass, the first listing there
        bool listedAtTheNewPath = false;
        runner.On(
            c => c.IsGit("worktree", "list") && c.GitDirectory() == Clone && !listedAtTheNewPath,
            (_, _) =>
            {
                listedAtTheNewPath = true;
                return Task.FromResult(ScriptedCommandRunner.Ok(Listing(Clone, paused)));
            });

        await using (RunningDaemon daemon = await Daemons.StartAsync(Home, new DaemonOptions { CommandRunner = runner, LoggerProvider = log }, None))
        {
            IReadOnlyList<string> calls = [.. runner.Commands.Select(c => string.Join(' ', c.Arguments))];
            int repaired = IndexOf(calls, $"--git-dir={Clone} worktree repair {paused}");
            // the software module's start sweep lists the worktrees of the clone at its new path
            int swept = calls.ToList().LastIndexOf($"--git-dir={Clone} worktree list --porcelain");
            Assert.True(IndexOf(calls, $"--git-dir={Earlier} worktree list --porcelain") < repaired);
            Assert.True(repaired < swept, "the clone is moved and repaired before the module starts");

            ProjectRecord registered = await RegisteredProjects.AddAsync(daemon, "gamma", None);

            Assert.True(Directory.Exists(Home.RepoClonePath(registered.Name, Repo)), "the clone exists once the registration returns");
            await daemon.StopAsync(None);
        }

        Assert.False(Directory.Exists(Home.EarlierClonesPath));
    }

    [Fact]
    public async Task A_registration_whose_clone_cannot_be_made_is_still_recorded_and_the_failure_logged()
    {
        runner.On(c => c.IsGit("init"), ScriptedCommandRunner.Fails(128, "fatal: cannot mkdir"));
        await using RunningDaemon daemon = await Daemons.StartAsync(Home, new DaemonOptions { CommandRunner = runner, LoggerProvider = log }, None);

        await RegisteredProjects.AddAsync(daemon, Project, None);

        Assert.NotNull(await daemon.Services.GetRequiredService<ProjectService>().ShowAsync(Principal.LocalOwner, Project, None));
        Assert.False(Directory.Exists(Clone));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("cannot mkdir", StringComparison.Ordinal));
        await daemon.StopAsync(None);
    }

    private const string EarlierConfig =
        "[core]\n\tbare = true\n[remote \"origin\"]\n\turl = https://github.com/example/repo.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n[branch \"coanda/aaaa1111\"]\n\tcoandaParcel = 01a1aa2b-396f-76c0-b4f7-561db6135f29\n";

    private static int IndexOf(IReadOnlyList<string> calls, string call)
    {
        int index = calls.ToList().IndexOf(call);
        Assert.True(index >= 0, $"no command '{call}' ran");
        return index;
    }

    private static void WriteEarlierClone(string clone)
    {
        Directory.CreateDirectory(Path.Combine(clone, "objects", "pack"));
        Directory.CreateDirectory(Path.Combine(clone, "refs", "heads", "coanda"));
        File.WriteAllText(Path.Combine(clone, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(clone, "config"), EarlierConfig);
        File.WriteAllText(Path.Combine(clone, "objects", "pack", "pack-1.pack"), "packed");
        File.WriteAllText(Path.Combine(clone, "refs", "heads", "coanda", "aaaa1111"), ScriptedCommandRunner.PrimaryHead);
    }

    /// <summary>Models a worktree listed by the earlier clone and, once its entry has moved with the clone, by the clone at its new path.</summary>
    private void ListedAtBothPaths(string worktree, string branch)
    {
        runner.AddWorktree(Earlier, worktree, branch, ScriptedCommandRunner.PrimaryHead);
        runner.AddWorktree(Clone, worktree, branch, ScriptedCommandRunner.PrimaryHead);
    }

    private static string Listing(string clone, params string[] worktrees) =>
        $"worktree {clone}\nbare\n\n" + string.Concat(worktrees.Select(w => $"worktree {w}\nHEAD {ScriptedCommandRunner.PrimaryHead}\nbranch refs/heads/coanda/x\n\n"));

    /// <summary>Registers <paramref name="project"/> in the home's store with no daemon running, so no clone is made.</summary>
    private async Task RegisterAsync(string project)
    {
        await using IDocumentStore store = await SeededStore.OpenAsync(Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, project, None);
    }

    /// <summary>Registers <paramref name="project"/> with a record holding two repos, written straight to the store.</summary>
    private Task<ProjectRecord> RegisterTwoReposAsync(string project) => RegisterReposAsync(project, "one", "two");

    /// <summary>Registers <paramref name="project"/> with a record holding a repo of each of <paramref name="repos"/>, written straight to the store.</summary>
    private async Task<ProjectRecord> RegisterReposAsync(string project, params string[] repos)
    {
        await using IDocumentStore store = await SeededStore.OpenAsync(Home.DatabasePath);
        var registered = new ProjectRegistered(
            Ids.New(), project, [.. repos.Select(r => new ProjectRepo(r, $"https://github.com/example/{Path.GetFileName(r)}.git"))], Principal.LocalOwner.Id, DateTimeOffset.UtcNow);
        await using IDocumentSession session = store.LightweightSession(project);
        session.Events.StartStream<ProjectRecord>(registered.ProjectId, registered);
        await session.SaveChangesAsync(None);
        return ProjectRecord.Create(registered);
    }

    /// <summary>What git reads, at the clone's new path, of the configuration the earlier clone carries there: its origin and the default refspec.</summary>
    private void CarriedConfiguration()
    {
        runner.SetConfig(Clone, "remote.origin.url", RegisteredProjects.Remote);
        runner.SetConfig(Clone, "remote.origin.fetch", RepoClones.DefaultRefspec);
    }

    /// <summary>Runs what the daemon's start runs of the clones, against the home's store, with <paramref name="commands"/> or the test's runner.</summary>
    private async Task StartAsync(ICommandRunner? commands = null, CancellationToken cancellationToken = default)
    {
        await using IDocumentStore store = await SeededStore.OpenAsync(Home.DatabasePath);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.AddProvider(log));
        await new RepoClones(store, Home, commands ?? runner, factory.CreateLogger<RepoClones>()).StartAsync(cancellationToken);
    }
}
