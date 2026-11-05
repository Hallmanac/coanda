using Coanda.Core.Projects;
using Coanda.Daemon.Store;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Projects;

/// <summary>
/// The core's bare clones of its projects' repos, one per repo at <see cref="NodeHome.RepoClonePath"/>,
/// which the core makes and holds and from which the software module cuts flow runs' worktrees. The core
/// knows a clone only as a remote, named <see cref="Git.Remote"/>, that it fetches and pushes refs to; the
/// branches and worktrees in it are the module's. Each clone fetches the remote's branches by the default
/// refspec and every node's ledger refs by <see cref="LedgerRefspec"/>.
/// </summary>
/// <remarks>
/// Every git command here runs at start, before any module starts, or at a project's registration, when
/// the project has no flow runs yet, so none overlaps a module's git command on the same clone. Making a
/// clone contacts no remote. Nothing here deletes or overwrites a clone, and nothing here fails a start
/// or a registration: what cannot be done is logged and left.
/// </remarks>
public sealed class RepoClones(IDocumentStore store, NodeHome home, ICommandRunner runner, ILogger<RepoClones> logger)
{
    /// <summary>The refspec <c>git remote add</c> gives a clone, which keeps the remote's branches as remote-tracking refs.</summary>
    public const string DefaultRefspec = $"+refs/heads/*:refs/remotes/{Git.Remote}/*";

    /// <summary>
    /// The namespace a clone fetches every node's ledger refs into from <see cref="Git.Remote"/>:
    /// <c>refs/coanda/nodes/&lt;node&gt;/…</c> on the remote lands at <c>refs/coanda/fetched/nodes/&lt;node&gt;/…</c>.
    /// It is apart from <c>refs/heads/</c>, <c>refs/tags/</c>, <c>refs/remotes/</c>, the
    /// <c>refs/coanda/nodes/</c> this node writes its own ledger refs under, and the module's
    /// <c>refs/coanda/rebase/</c>, so a fetch never moves a ref this node writes and no branch maps onto it.
    /// </summary>
    public const string LedgerNamespace = "refs/coanda/fetched/nodes/";

    /// <summary>
    /// The refspec a clone fetches the ledger by, beside <see cref="DefaultRefspec"/>. It is forced, so a
    /// peer's rewritten ref is fetched as it stands and import, not git, judges it.
    /// </summary>
    public const string LedgerRefspec = $"+refs/coanda/nodes/*:{LedgerNamespace}*";

    /// <summary>The name a build from before the per-repo layout gave a project's one clone, under <c>clones/&lt;project&gt;/</c>.</summary>
    public const string EarlierCloneName = "repo.git";

    private static readonly string FetchKey = $"remote.{Git.Remote}.fetch";

    private readonly Git git = new(runner);

    /// <summary>
    /// What the start does, before any module starts: moves each clone an earlier build kept under
    /// <see cref="NodeHome.EarlierClonesPath"/> to the per-repo layout (<see cref="MoveEarlierAsync"/>), then
    /// makes sure every repo of every registered project has its clone (<see cref="EnsureAsync"/>), and
    /// repairs every linked worktree each clone lists whose directory exists. The repair is a no-op for a
    /// worktree whose links are sound, and running it at every start means a move whose repairs were cut
    /// short, by a stopped start or a repair that failed, is finished by the next one.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Project> projects;
        await using (IQuerySession session = store.QuerySession(Tenants.Node))
        {
            projects = await session.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
        }

        await MoveEarlierAsync(projects, cancellationToken);
        foreach (Project project in projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            await EnsureAsync(project, repairWorktrees: true, cancellationToken);
        }
    }

    /// <summary>
    /// Moves each <c>clones/&lt;project&gt;/repo.git</c> under the home to the clone path of the project's
    /// one repo. Its worktrees are listed before the move, and a clone whose worktrees cannot be listed is
    /// not moved. The worktrees are repaired from the clone at its new path after it, by the start's
    /// <see cref="EnsureAsync"/>, which repairs every clone's worktrees at every start, so git works inside
    /// them again even when this start stops before it gets that far. A clone that cannot be placed is left where it is and the reason logged. A project's directory under
    /// <c>clones/</c>, and <c>clones/</c> itself, is removed once nothing is left in it.
    /// </summary>
    public async Task MoveEarlierAsync(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        string earlier = home.EarlierClonesPath;
        if (!Directory.Exists(earlier))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(earlier).Order(StringComparer.Ordinal))
        {
            string clone = Path.Combine(directory, EarlierCloneName);
            if (Directory.Exists(clone))
            {
                try
                {
                    await MoveAsync(Path.GetFileName(directory), clone, projects, cancellationToken);
                }
                catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogError(failure, "The clone at {Clone} could not be moved to the per-repo layout; it is left where it is.", clone);
                }
            }

            RemoveIfEmpty(directory);
        }

        RemoveIfEmpty(earlier);
    }

    /// <summary>
    /// Makes sure each repo of <paramref name="project"/> has its bare clone, whose origin is the repo's
    /// recorded remote and which fetches by <see cref="DefaultRefspec"/> and <see cref="LedgerRefspec"/>.
    /// A missing clone is made beside its path, from no remote contact, and moved there once complete. An
    /// existing one whose origin is the recorded remote gains whichever refspec it lacks, and is otherwise
    /// left as it is; one whose origin is any other, and anything at the path that is not a bare clone, is
    /// left and logged. A project whose name is not a plain directory name has no clones. A failure is
    /// logged, never thrown.
    /// </summary>
    public Task EnsureAsync(Project project, CancellationToken cancellationToken) =>
        EnsureAsync(project, repairWorktrees: false, cancellationToken);

    private async Task EnsureAsync(Project project, bool repairWorktrees, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!RepoLayout.IsPlain(project.Name))
        {
            logger.LogWarning("Project '{Project}' is not a plain directory name, so the node holds no clone of its repos.", project.Name);
            return;
        }

        foreach (ProjectRepo repo in project.Repos)
        {
            if (!RepoLayout.IsPlain(repo.Name))
            {
                logger.LogWarning("Repo '{Repo}' of project '{Project}' is not a plain directory name, so the node holds no clone of it.", repo.Name, project.Name);
                continue;
            }

            try
            {
                await EnsureCloneAsync(project.Name, repo, repairWorktrees, cancellationToken);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(
                    failure,
                    "The clone of repo '{Repo}' of project '{Project}' at {Clone} could not be made or checked; the next start tries again.",
                    repo.Name, project.Name, home.RepoClonePath(project.Name, repo.Name));
            }
        }
    }

    private async Task MoveAsync(string name, string clone, IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        if (projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal)) is not { } project)
        {
            logger.LogWarning("The clone at {Clone} is left where it is: no project named '{Project}' is registered on the node.", clone, name);
            return;
        }

        if (!RepoLayout.IsPlain(project.Name))
        {
            logger.LogWarning("The clone at {Clone} is left where it is: project '{Project}' is not a plain directory name, so it has no clones.", clone, project.Name);
            return;
        }

        if (project.Repos is not [ProjectRepo repo])
        {
            logger.LogWarning(
                "The clone at {Clone} is left where it is: project '{Project}' has {Count} repos, so which one it is a clone of is not known.",
                clone, project.Name, project.Repos.Count);
            return;
        }

        string destination = home.RepoClonePath(project.Name, repo.Name);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            logger.LogWarning("The clone at {Clone} is left where it is: something is already at {Destination}, where the clone of repo '{Repo}' goes.", clone, destination, repo.Name);
            return;
        }

        CommandLine list = Git.Against(clone, "worktree", "list", "--porcelain");
        CommandResult listed = await git.TryRunAsync(list, cancellationToken);
        if (listed is not { Succeeded: true, Truncated: false })
        {
            logger.LogWarning("The clone at {Clone} is left where it is: its worktrees could not be listed, so moving it could strand them. {Reason}", clone, new GitCommandException(list, listed).Message);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
        Directory.Move(clone, destination);
        logger.LogInformation("Moved the clone of repo '{Repo}' of project '{Project}' from {Clone} to {Destination}.", repo.Name, project.Name, clone, destination);
    }

    /// <summary>
    /// Repairs each linked worktree the clone at <paramref name="clone"/> lists whose directory exists, so
    /// the worktree and the clone point at each other wherever the clone now is. A listing or a repair that
    /// fails is logged, and the next start tries again.
    /// </summary>
    private async Task RepairWorktreesAsync(string clone, CancellationToken cancellationToken)
    {
        CommandLine list = Git.Against(clone, "worktree", "list", "--porcelain");
        CommandResult listed = await git.TryRunAsync(list, cancellationToken);
        if (listed is not { Succeeded: true, Truncated: false })
        {
            logger.LogError("The worktrees of the clone at {Clone} could not be listed, so none was repaired; the next start tries again. {Reason}", clone, new GitCommandException(list, listed).Message);
            return;
        }

        foreach (string worktree in LinkedWorktrees(listed.StandardOutput).Where(Directory.Exists))
        {
            await RepairAsync(clone, worktree, cancellationToken);
        }
    }

    /// <summary>Points the worktree at <paramref name="worktree"/> and the clone at <paramref name="clone"/> back at each other; a failure is logged naming the worktree.</summary>
    private async Task RepairAsync(string clone, string worktree, CancellationToken cancellationToken)
    {
        CommandLine repair = Git.Against(clone, "worktree", "repair", worktree);
        CommandResult repaired = await git.TryRunAsync(repair, cancellationToken);
        if (!repaired.Succeeded)
        {
            logger.LogError("The worktree at {Worktree} could not be repaired from its clone at {Clone}; the next start tries again. {Reason}", worktree, clone, new GitCommandException(repair, repaired).Message);
        }
    }

    private async Task EnsureCloneAsync(string project, ProjectRepo repo, bool repairWorktrees, CancellationToken cancellationToken)
    {
        string clone = home.RepoClonePath(project, repo.Name);
        if (Directory.Exists(clone) || File.Exists(clone))
        {
            await CheckAsync(project, repo, clone, repairWorktrees, cancellationToken);
            return;
        }

        // A repo's name never starts with a dot, so the partial clone's name is never a repo's.
        string parent = Path.GetDirectoryName(clone) ?? clone;
        string partial = Path.Combine(parent, $".{repo.Name}.partial");
        Directory.CreateDirectory(parent);
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        await git.RunAsync(new CommandLine("git", ["init", "--bare", "--quiet", partial], parent, Git.Environment) { Limit = Git.Limit }, cancellationToken);
        await git.RunAsync(Git.Against(partial, "remote", "add", Git.Remote, repo.Remote), cancellationToken);
        await git.RunAsync(Git.Against(partial, "config", "--add", FetchKey, LedgerRefspec), cancellationToken);
        Directory.Move(partial, clone);
        logger.LogInformation("Made the clone of repo '{Repo}' of project '{Project}' at {Clone}.", repo.Name, project, clone);
    }

    /// <summary>
    /// Checks an existing clone: one that is bare has its worktrees repaired when <paramref name="repairWorktrees"/>
    /// says so, whatever its origin, and with the recorded origin gains any refspec it lacks; anything else
    /// is logged and left.
    /// </summary>
    private async Task CheckAsync(string project, ProjectRepo repo, string clone, bool repairWorktrees, CancellationToken cancellationToken)
    {
        CommandResult bare = await git.TryRunAsync(Git.Against(clone, "rev-parse", "--is-bare-repository"), cancellationToken);
        if (!bare.Succeeded || bare.StandardOutput.Trim() != "true")
        {
            logger.LogWarning(
                "{Clone} is where the node keeps its clone of repo '{Repo}' of project '{Project}', and what is there is not a bare clone, so it is left as it is; move it aside and the next start makes the clone.",
                clone, repo.Name, project);
            return;
        }

        if (repairWorktrees)
        {
            await RepairWorktreesAsync(clone, cancellationToken);
        }

        IReadOnlyList<string> origin = await ValuesAsync(clone, $"remote.{Git.Remote}.url", cancellationToken);
        if (origin is not [string url] || !string.Equals(url, repo.Remote, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "The clone at {Clone} is left as it is: its {Remote} remote is {Origin}, not {Recorded}, the remote project '{Project}' records for repo '{Repo}'.",
                clone, Git.Remote, origin.Count == 0 ? "not set" : OutputText.Redact(string.Join(", ", origin)), OutputText.Redact(repo.Remote), project, repo.Name);
            return;
        }

        IReadOnlyList<string> fetches = await ValuesAsync(clone, FetchKey, cancellationToken);
        foreach (string refspec in new[] { DefaultRefspec, LedgerRefspec }.Where(r => !fetches.Contains(r, StringComparer.Ordinal)))
        {
            await git.RunAsync(Git.Against(clone, "config", "--add", FetchKey, refspec), cancellationToken);
            logger.LogInformation("The clone at {Clone} now fetches by {Refspec}.", clone, refspec);
        }
    }

    /// <summary>Every value <paramref name="key"/> has in the clone's configuration, none when it has none.</summary>
    private async Task<IReadOnlyList<string>> ValuesAsync(string clone, string key, CancellationToken cancellationToken)
    {
        CommandLine get = Git.Against(clone, "config", "--get-all", key);
        CommandResult got = await git.TryRunAsync(get, cancellationToken);
        return got switch
        {
            { Succeeded: true, Truncated: false } => [.. Lines(got.StandardOutput)],
            { ExitCode: 1, TimedOut: false } => [],
            _ => throw new GitCommandException(get, got),
        };
    }

    /// <summary>The paths of the linked worktrees a porcelain listing names: every entry but the bare clone's own.</summary>
    private static IReadOnlyList<string> LinkedWorktrees(string porcelain)
    {
        List<string> paths = [];
        foreach (string entry in porcelain.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string[] lines = entry.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines is [string first, ..] && first.StartsWith("worktree ", StringComparison.Ordinal) && !lines.Contains("bare", StringComparer.Ordinal))
            {
                paths.Add(first["worktree ".Length..]);
            }
        }

        return paths;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0);

    private void RemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(failure, "{Directory} is empty and could not be removed; it is left.", directory);
        }
    }
}
