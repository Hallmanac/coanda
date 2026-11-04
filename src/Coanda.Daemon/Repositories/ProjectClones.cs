using System.Collections.Concurrent;
using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Daemon.Commands;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Repositories;

/// <summary>What checking a flow's worktree out came to: ready on its branch at a head, or refused with a reason.</summary>
public abstract record Checkout
{
    private Checkout()
    {
    }

    /// <summary>The flow's work path is a worktree on <paramref name="Branch"/>, with <paramref name="Head"/> checked out.</summary>
    public sealed record Ready(string Branch, string Head) : Checkout;

    /// <summary>The worktree was not made, for <paramref name="Reason"/>; nothing was created.</summary>
    public sealed record Refused(string Reason) : Checkout;
}

/// <summary>
/// The node's clones of its projects' repositories and the flows' worktrees cut from them. A clone
/// is bare, made the first time any flow in the project needs it, with the project's remote named
/// <see cref="Git.Remote"/> and the default fetch refspec, so the remote's branches are kept only as
/// remote-tracking refs and the only local branches are the ones the worktree step creates. Every
/// git command against one clone runs one at a time, across all of the project's flows.
/// </summary>
/// <remarks>
/// A flow's branch is <c>coanda/&lt;short parcel id&gt;</c>, and the parcel that owns it is recorded
/// in the clone's configuration as <c>branch.&lt;branch&gt;.coandaParcel</c> before the branch is
/// created, so an interruption leaves a setting with no branch, never a branch with no owner. The
/// clone's <c>info/exclude</c>, which every worktree of it shares, holds <see cref="ExcludeLine"/>,
/// so the daemon's own files at a worktree's root are invisible to git there.
/// </remarks>
public sealed class ProjectClones(NodeHome home, ICommandRunner runner, ILogger<ProjectClones> logger)
{
    /// <summary>The line in the clone's <c>info/exclude</c> that hides the daemon's directory at each worktree's root.</summary>
    public const string ExcludeLine = "/.coanda/";

    /// <summary>The setting, under a branch's section of the clone's configuration, naming the parcel that owns it.</summary>
    public const string OwnerSetting = "coandaParcel";

    private readonly Git git = new(runner);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);

    /// <summary>The branch a parcel's flows work on.</summary>
    public static string BranchOf(Guid parcelId) => $"coanda/{Ids.Short(parcelId)}";

    /// <summary>The configuration key naming the parcel that owns <paramref name="branch"/>.</summary>
    public static string OwnerKeyOf(string branch) => $"branch.{branch}.{OwnerSetting}";

    /// <summary>
    /// Makes the flow's work path a worktree of the project's clone on the parcel's branch. A work
    /// path that already holds it is answered as it stands. A branch the clone does not hold is cut
    /// from the primary branch as a fetch brings it; one it holds, which an earlier flow on the parcel
    /// created, is checked out as it stands with nothing fetched. A failing git command throws
    /// <see cref="GitCommandException"/>.
    /// </summary>
    public async Task<Checkout> CheckOutAsync(Project project, Guid parcelId, Guid flowId, CancellationToken cancellationToken)
    {
        string clone = home.ProjectClonePath(project.Name);
        string workPath = home.FlowWorkPath(project.Name, flowId);
        string branch = BranchOf(parcelId);
        string reference = $"refs/heads/{branch}";

        using Held held = await LockAsync(clone, cancellationToken);
        if (await EnsureCloneAsync(project, clone, cancellationToken) is { } unrecognized)
        {
            return new Checkout.Refused(unrecognized);
        }

        EnsureExcluded(clone);
        IReadOnlyList<ListedWorktree> listed = await ListAsync(clone, cancellationToken);
        ListedWorktree? here = listed.FirstOrDefault(w => !w.Bare && RealPath.Same(w.Path, workPath));
        if (here is { Stale: false, Head: { } checkedOut } && here.Branch == reference)
        {
            return new Checkout.Ready(branch, checkedOut);
        }

        string tip = (await git.RunAsync(Git.Against(clone, "for-each-ref", "--format=%(objectname)", reference), cancellationToken)).Trim();
        bool exists = tip.Length > 0;
        if (exists)
        {
            string owner = (await git.RunAsync(Git.Against(clone, "config", "--get", "--default", string.Empty, OwnerKeyOf(branch)), cancellationToken)).Trim();
            if (!string.Equals(owner, parcelId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                string whose = owner.Length == 0 ? "names no parcel as its owner" : $"belongs to parcel {owner}";
                return new Checkout.Refused(
                    $"Branch {branch} already exists in the node's clone of project '{project.Name}' and {whose}, so it is not checked out for parcel {parcelId}.");
            }
        }
        else
        {
            await git.RunAsync(
                Git.Against(clone, "fetch", "--no-tags", "--quiet", Git.Remote, $"+refs/heads/{project.PrimaryBranch}:refs/remotes/{Git.Remote}/{project.PrimaryBranch}"),
                cancellationToken);
            tip = (await git.RunAsync(
                Git.Against(clone, "rev-parse", "--verify", "--quiet", $"refs/remotes/{Git.Remote}/{project.PrimaryBranch}^{{commit}}"),
                cancellationToken)).Trim();
        }

        // -z lists each path verbatim, where the default quotes a path holding anything unusual.
        string tracked = await git.RunAsync(Git.Against(clone, "ls-tree", "-r", "-z", "--name-only", tip, "--", ".coanda"), cancellationToken);
        string[] underCoanda = [.. tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => p.StartsWith(".coanda/", StringComparison.Ordinal))];
        if (underCoanda.Length > 0)
        {
            string from = exists ? $"branch {branch}" : $"primary branch {project.PrimaryBranch}";
            return new Checkout.Refused(
                $"Commit {tip} of {from} tracks files under .coanda/, the directory the daemon keeps its own files in at a worktree's root, so no worktree is made from it: {string.Join(", ", underCoanda)}.");
        }

        foreach (ListedWorktree stale in listed.Where(w => !w.Bare && w.Stale && (w.Branch == reference || RealPath.Same(w.Path, workPath))))
        {
            await RemoveEntryAsync(clone, stale, cancellationToken);
        }

        if (!exists)
        {
            await git.RunAsync(Git.Against(clone, "config", OwnerKeyOf(branch), parcelId.ToString()), cancellationToken);
            await git.RunAsync(Git.Against(clone, "branch", "--no-track", branch, tip), cancellationToken);
        }

        await git.RunAsync(Git.Against(clone, "worktree", "add", "--quiet", workPath, branch), cancellationToken);
        return new Checkout.Ready(branch, tip);
    }

    /// <summary>
    /// Removes the flow's worktree, any uncommitted changes in it included, logging the paths of the
    /// changes it discards; the branch and its commits stay in the clone. A flow whose project has no
    /// clone, or whose work path the clone does not list, is left alone. Returns whether a worktree
    /// was removed; a failing git command throws <see cref="GitCommandException"/>.
    /// </summary>
    public async Task<bool> RemoveAsync(string project, Guid flowId, CancellationToken cancellationToken)
    {
        // A project whose name is no plain directory name has no clone, and its flows no worktree.
        if (!NodeHome.IsPlain(project))
        {
            return false;
        }

        string clone = home.ProjectClonePath(project);
        if (!Directory.Exists(clone))
        {
            return false;
        }

        string workPath = home.FlowWorkPath(project, flowId);
        using Held held = await LockAsync(clone, cancellationToken);
        ListedWorktree? entry = (await ListAsync(clone, cancellationToken)).FirstOrDefault(w => !w.Bare && RealPath.Same(w.Path, workPath));
        if (entry is null)
        {
            return false;
        }

        await DiscardAsync(clone, entry, flowId, cancellationToken);
        return true;
    }

    /// <summary>
    /// Removes, as <see cref="RemoveAsync"/> does, every worktree an existing clone lists whose path is
    /// the work path of a flow that <paramref name="isFinal"/> says has ended or been stopped. A listed
    /// path that is no flow's work path, the clone's own included, is left alone, and so is the
    /// worktree of a flow the store does not hold or that is still live. A clone, or a worktree, that
    /// cannot be swept is logged and passed over.
    /// </summary>
    public async Task SweepAsync(Func<string, Guid, CancellationToken, Task<bool>> isFinal, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(home.ClonesPath))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(home.ClonesPath))
        {
            string project = Path.GetFileName(directory);
            try
            {
                await SweepCloneAsync(project, isFinal, cancellationToken);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(failure, "Sweeping the worktrees of project '{Project}' failed; they are left as they are.", project);
            }
        }
    }

    private async Task SweepCloneAsync(string project, Func<string, Guid, CancellationToken, Task<bool>> isFinal, CancellationToken cancellationToken)
    {
        string clone = home.ProjectClonePath(project);
        if (!Directory.Exists(clone))
        {
            return;
        }

        using Held held = await LockAsync(clone, cancellationToken);
        foreach (ListedWorktree entry in await ListAsync(clone, cancellationToken))
        {
            if (entry.Bare || FlowOf(project, entry.Path) is not Guid flowId || !await isFinal(project, flowId, cancellationToken))
            {
                continue;
            }

            try
            {
                await DiscardAsync(clone, entry, flowId, cancellationToken);
                logger.LogInformation("Removed the worktree of flow {Flow}, which has ended or been stopped, at {Path}.", Ids.Short(flowId), entry.Path);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(failure, "Removing the worktree of flow {Flow} at {Path} failed; it is left as it is.", Ids.Short(flowId), entry.Path);
            }
        }
    }

    /// <summary>The flow whose work path, in <paramref name="project"/>, is <paramref name="path"/>, or null when it is no flow's.</summary>
    private Guid? FlowOf(string project, string path) =>
        Guid.TryParse(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), out Guid flowId)
            && RealPath.Same(path, home.FlowWorkPath(project, flowId))
            ? flowId
            : null;

    /// <summary>
    /// Makes the clone when the project has none, in a directory beside its path that is moved there
    /// only once the clone has succeeded; a directory an earlier clone ended part way left there is
    /// deleted first. Returns the reason to refuse when something git does not recognize as a
    /// repository is at the clone's path, which is never deleted.
    /// </summary>
    private async Task<string?> EnsureCloneAsync(Project project, string clone, CancellationToken cancellationToken)
    {
        if (Directory.Exists(clone) || File.Exists(clone))
        {
            CommandResult check = await git.TryRunAsync(Git.Against(clone, "rev-parse", "--is-bare-repository"), cancellationToken);
            return check.Succeeded
                ? null
                : $"{clone} is where the node keeps its clone of project '{project.Name}', and git does not recognize what is there as a repository; move it aside and the step clones the project again.";
        }

        string partial = home.ProjectClonePartialPath(project.Name);
        string parent = Path.GetDirectoryName(clone) ?? home.ClonesPath;
        Directory.CreateDirectory(parent);
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        await git.RunAsync(new CommandLine("git", ["init", "--bare", "--quiet", partial], parent, Git.Environment) { Limit = Git.Limit }, cancellationToken);
        await git.RunAsync(Git.Against(partial, "remote", "add", Git.Remote, project.Remote), cancellationToken);
        Directory.Move(partial, clone);
        return null;
    }

    /// <summary>Makes sure the clone's <c>info/exclude</c> holds <see cref="ExcludeLine"/> exactly once, writing it only when it does not.</summary>
    private static void EnsureExcluded(string clone)
    {
        string exclude = Path.Combine(clone, "info", "exclude");
        string[] lines = File.Exists(exclude) ? File.ReadAllText(exclude).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n') : [];
        if (lines.Count(l => l == ExcludeLine) == 1)
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(clone, "info"));
        string[] kept = [.. lines.Where(l => l.Length > 0 && l != ExcludeLine), ExcludeLine];
        File.WriteAllText(exclude, string.Join('\n', kept) + "\n");
    }

    private async Task<IReadOnlyList<ListedWorktree>> ListAsync(string clone, CancellationToken cancellationToken) =>
        ListedWorktree.Parse(await git.RunAsync(Git.Against(clone, "worktree", "list", "--porcelain"), cancellationToken));

    /// <summary>Removes a listed worktree after logging the paths of the uncommitted changes in it that the removal discards.</summary>
    private async Task DiscardAsync(string clone, ListedWorktree entry, Guid flowId, CancellationToken cancellationToken)
    {
        if (!entry.Prunable && Directory.Exists(entry.Path))
        {
            CommandResult status = await git.TryRunAsync(Git.In(entry.Path, "status", "--porcelain", "--untracked-files=all"), cancellationToken);
            string[] changed = status.Succeeded ? [.. Lines(status.StandardOutput).Select(l => l.Length > 3 ? l[3..] : l)] : [];
            if (!status.Succeeded)
            {
                logger.LogWarning(
                    "The uncommitted changes in the worktree of flow {Flow} at {Path} could not be listed (git status exited with code {Code}); the removal discards them unlisted.",
                    Ids.Short(flowId), entry.Path, status.ExitCode);
            }
            else if (status.Truncated)
            {
                logger.LogWarning(
                    "Removing the worktree of flow {Flow} at {Path} discards more uncommitted changes than can be listed; the last of them are to: {Changes}",
                    Ids.Short(flowId), entry.Path, string.Join(", ", changed));
            }
            else if (changed.Length > 0)
            {
                logger.LogWarning(
                    "Removing the worktree of flow {Flow} at {Path} discards uncommitted changes to: {Changes}",
                    Ids.Short(flowId), entry.Path, string.Join(", ", changed));
            }
        }

        await RemoveEntryAsync(clone, entry, cancellationToken);
    }

    /// <summary>
    /// Removes a worktree entry from the clone. An entry whose directory is gone is pruned, which is
    /// all git keeps of it, after it is unlocked when locked; any other is removed forcibly, its
    /// uncommitted changes and a lock included.
    /// </summary>
    private async Task RemoveEntryAsync(string clone, ListedWorktree entry, CancellationToken cancellationToken)
    {
        bool gone = entry.Prunable || entry.Locked is not null && !Directory.Exists(entry.Path);
        if (!gone)
        {
            await git.RunAsync(Git.Against(clone, "worktree", "remove", "--force", "--force", entry.Path), cancellationToken);
            return;
        }

        if (entry.Locked is not null)
        {
            await git.RunAsync(Git.Against(clone, "worktree", "unlock", entry.Path), cancellationToken);
        }

        await git.RunAsync(Git.Against(clone, "worktree", "prune"), cancellationToken);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0);

    private async Task<Held> LockAsync(string clone, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = locks.GetOrAdd(clone, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Held(gate);
    }

    /// <summary>One clone's turn at running git commands, given back when disposed.</summary>
    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
