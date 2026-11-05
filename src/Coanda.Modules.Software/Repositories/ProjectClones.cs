using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Coanda.Modules.Software.Steps;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;
using Microsoft.Extensions.Logging;

namespace Coanda.Modules.Software.Repositories;

/// <summary>What checking a flow run's worktree out came to: ready on its branch at a head, or refused with a reason.</summary>
public abstract record Checkout
{
    private Checkout()
    {
    }

    /// <summary>The flow run's work path is a worktree on <paramref name="Branch"/>, with <paramref name="Head"/> checked out.</summary>
    public sealed record Ready(string Branch, string Head) : Checkout;

    /// <summary>The worktree was not made, for <paramref name="Reason"/>; nothing was created.</summary>
    public sealed record Refused(string Reason) : Checkout;
}

/// <summary>What pushing a flow run's branch came to: pushed at a head, or not pushed, for one of three reasons.</summary>
public abstract record Push
{
    private Push()
    {
    }

    /// <summary>The remote's branch holds <paramref name="Head"/>, and so does the clone's remote-tracking ref for it.</summary>
    public sealed record Pushed(string Head) : Push;

    /// <summary>Nothing was pushed, because the worktree holds the changes <paramref name="Changes"/> lists, which git status reports.</summary>
    public sealed record Unclean(IReadOnlyList<string> Changes) : Push;

    /// <summary>Git refused the push because the remote's branch did not hold what the lease expected: someone else updated it.</summary>
    public sealed record Stale(string Reason) : Push;

    /// <summary>The push failed or was rejected for <paramref name="Reason"/>, which quotes git with any URL's user information removed.</summary>
    public sealed record Failed(string Reason) : Push;

    /// <summary>Nothing was pushed, because the repo has no clone the module can use, for <paramref name="Reason"/> (<see cref="ProjectClones.UnusableAsync"/>).</summary>
    public sealed record NoClone(string Reason) : Push;
}

/// <summary>What rebasing a flow run's branch came to: pushed at a new head, or not pushed, for one of five reasons.</summary>
public abstract record Rebase
{
    private Rebase()
    {
    }

    /// <summary>
    /// The remote's branch, the clone's branch checked out in the worktree, and the clone's
    /// remote-tracking ref for it all hold <paramref name="Head"/>, the rebased commit.
    /// </summary>
    public sealed record Pushed(string Head) : Rebase;

    /// <summary>Nothing was fetched, rebased, or pushed, because the worktree holds the changes <paramref name="Changes"/> lists.</summary>
    public sealed record Unclean(IReadOnlyList<string> Changes) : Rebase;

    /// <summary>
    /// Nothing was fetched, rebased, or pushed, because the branch's tip, <paramref name="Tip"/>, is
    /// not the recorded head and the remote's branch, at <paramref name="Remote"/> (empty when it has
    /// none), is no commit an earlier performance noted and pushed.
    /// </summary>
    public sealed record Moved(string Tip, string Remote) : Rebase;

    /// <summary>
    /// The rebase stopped on a conflict at <paramref name="Commit"/>, where git reports
    /// <paramref name="Paths"/> unmerged; it was aborted and the branch checked out again at the recorded head.
    /// </summary>
    public sealed record Conflict(string Commit, IReadOnlyList<string> Paths) : Rebase;

    /// <summary>Git refused the push because the remote's branch did not hold the recorded head: someone else updated it.</summary>
    public sealed record Stale(string Reason) : Rebase;

    /// <summary>The push failed or was rejected for <paramref name="Reason"/>, which quotes git with any URL's user information removed.</summary>
    public sealed record Failed(string Reason) : Rebase;

    /// <summary>Nothing was rebased or pushed, because the repo has no clone the module can use, for <paramref name="Reason"/> (<see cref="ProjectClones.UnusableAsync"/>).</summary>
    public sealed record NoClone(string Reason) : Rebase;
}

/// <summary>
/// A flow run as the worktree step reads it when its worktree may hold the parcel's branch: its status
/// as the core names it (running, paused, ended, or stopped), and the work path the core gives it, null
/// when it gives none.
/// </summary>
public sealed record WorktreeHolder(string Status, string? WorkPath);

/// <summary>
/// What the worktree step reads of the flow runs on the node when another worktree holds the parcel's
/// branch: a flow run as the core shows it (null when the core holds no such flow run), and a wait that
/// completes once nothing of the core's runs for an ended or stopped flow run any more: when its ended or
/// stopped feed delivery reaches the module, or when the core shows it no longer working
/// (<see cref="Steps.WorktreeStep"/>).
/// </summary>
public sealed record WorktreeHolders(
    Func<Guid, CancellationToken, Task<WorktreeHolder?>> Of,
    Func<Guid, CancellationToken, Task> EndReached);

/// <summary>
/// The flow runs' worktrees, cut from the core's clones of the projects' repos. The core makes and holds
/// one bare clone per repo, at <c>projects/&lt;project&gt;/repos/&lt;repo&gt;</c> under the node home
/// (<see cref="RepoLayout.ClonePath"/>), with the repo's remote named <see cref="Git.Remote"/>, so the
/// remote's branches are kept only as remote-tracking refs and the only local branches are the ones the
/// worktree step creates. The module finds a repo's clone by that layout and makes none: a clone that
/// is missing, or is not a repository, refuses the step (<see cref="UnusableAsync"/>). A flow run's
/// worktree is cut at the work path its dispatch carries, and a worktree the module did not cut is told
/// from a flow run's by the work path the core shows for that flow run. Every git command of the module's
/// against one clone runs one at a time, across all of the project's flow runs.
/// </summary>
/// <remarks>
/// A flow run's branch is <c>coanda/&lt;short parcel id&gt;</c>, and the parcel that owns it is recorded
/// in the clone's configuration as <c>branch.&lt;branch&gt;.coandaParcel</c> before the branch is
/// created, so an interruption leaves a setting with no branch, never a branch with no owner. The
/// clone's <c>info/exclude</c>, which every worktree of it shares, holds <see cref="ExcludeLine"/>,
/// so the daemon's own files at a worktree's root are invisible to git there.
/// </remarks>
public sealed class ProjectClones(string homePath, ICommandRunner runner, ILogger<ProjectClones> logger)
{
    /// <summary>The line in the clone's <c>info/exclude</c> that hides the daemon's directory at each worktree's root.</summary>
    public const string ExcludeLine = "/.coanda/";

    /// <summary>The setting, under a branch's section of the clone's configuration, naming the parcel that owns it.</summary>
    public const string OwnerSetting = "coandaParcel";

    private readonly Git git = new(runner);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);

    /// <summary>Where the core keeps its clone of the repo <paramref name="target"/> acts on.</summary>
    public string ClonePathOf(RepoTarget target) => RepoLayout.ClonePath(homePath, target.Project, target.Repo);

    /// <summary>The branch a parcel's flow runs work on.</summary>
    public static string BranchOf(Guid parcelId) => $"coanda/{ShortId.Of(parcelId)}";

    /// <summary>The configuration key naming the parcel that owns <paramref name="branch"/>.</summary>
    public static string OwnerKeyOf(string branch) => $"branch.{branch}.{OwnerSetting}";

    /// <summary>
    /// Makes the flow run's work path, <paramref name="workPath"/>, the one its dispatch carries, a
    /// worktree of the repo's clone on the parcel's branch. A clone that is missing or is not a
    /// repository refuses the checkout and makes nothing (<see cref="UnusableAsync"/>). A work
    /// path that already holds it is answered as it stands. Otherwise the remote's branch of that
    /// name is fetched first, so the clone's remote-tracking ref for it, which the push's lease
    /// expects, holds what the remote holds now, or is deleted when the remote has no such branch.
    /// A branch the clone does not hold is cut from the remote's branch when there is one, and from
    /// the primary branch as a fetch brings it when there is not. One it holds, which an earlier flow run
    /// on the parcel created, moves forward to the remote's branch when that contains it, so commits
    /// someone added on the remote are built on rather than refused at the push. When each has
    /// commits the other does not, and the remote's branch is <paramref name="lastPushed"/>, the
    /// commit coanda last recorded as pushed for the parcel, the divergence is a local rewrite (a
    /// person's rebase, say) and the branch is checked out as it stands; when the remote's is any
    /// other commit, the checkout is refused naming how to reconcile them.
    /// </summary>
    /// <remarks>
    /// A branch another worktree has checked out is taken over when that worktree is at the work path the
    /// core gives a flow run <paramref name="holders"/> reports ended or stopped, as a build flow run before a merge flow run
    /// can be while its removal is still pending: the clone's turn is given back until nothing the core runs
    /// for that flow run is still running, which its end reaching the module through the feed or the core
    /// showing it no longer working tells, and then, in one turn, the worktree is listed again, the holding one removed as
    /// its end's delivery would, and the branch checked out here. A later removal of the ended flow run's
    /// worktree works by that flow run's own work path and finds nothing there. A branch held by a live
    /// flow run's worktree, or by one at a path that is not the work path the core gives the flow run its
    /// last segment names, or whose flow run the core does not hold, is refused naming it. A failing git command throws <see cref="GitCommandException"/>.
    /// </remarks>
    public async Task<Checkout> CheckOutAsync(
        RepoTarget project,
        Guid parcelId,
        Guid flowRunId,
        string workPath,
        string? lastPushed,
        WorktreeHolders holders,
        CancellationToken cancellationToken)
    {
        string clone = ClonePathOf(project);
        HashSet<Guid> reached = [];
        while (true)
        {
            Guid? waitFor;
            using (Held held = await LockAsync(clone, cancellationToken))
            {
                (Checkout? checkout, waitFor) = await CheckOutHeldAsync(project, parcelId, flowRunId, workPath, lastPushed, holders, reached, cancellationToken);
                if (checkout is not null)
                {
                    return checkout;
                }
            }

            // Outside the clone's turn: the holding flow run's end, if it arrives meanwhile, removes its worktree through it.
            Guid holder = waitFor ?? throw new InvalidOperationException("A checkout neither ended nor named a flow run to wait for.");
            await holders.EndReached(holder, cancellationToken);
            reached.Add(holder);
        }
    }

    /// <summary>
    /// <see cref="CheckOutAsync"/> within one turn at the clone: the checkout, or the ended or stopped
    /// flow run whose end must reach the module before its worktree is taken over, when it is not in
    /// <paramref name="reached"/> yet.
    /// </summary>
    private async Task<(Checkout? Checkout, Guid? WaitFor)> CheckOutHeldAsync(
        RepoTarget project,
        Guid parcelId,
        Guid flowRunId,
        string workPath,
        string? lastPushed,
        WorktreeHolders holders,
        HashSet<Guid> reached,
        CancellationToken cancellationToken)
    {
        string clone = ClonePathOf(project);
        string branch = BranchOf(parcelId);
        string reference = $"refs/heads/{branch}";

        if (await UnusableAsync(project, cancellationToken) is { } unusable)
        {
            return (new Checkout.Refused(unusable), null);
        }

        EnsureExcluded(clone);
        IReadOnlyList<ListedWorktree> listed = await ListAsync(clone, cancellationToken);
        ListedWorktree? here = listed.FirstOrDefault(w => !w.Bare && RealPath.Same(w.Path, workPath));
        if (here is { Stale: false, Head: { } checkedOut } && here.Branch == reference)
        {
            return (new Checkout.Ready(branch, checkedOut), null);
        }

        string tip = await RefAsync(clone, reference, cancellationToken);
        bool exists = tip.Length > 0;
        if (exists)
        {
            string owner = (await git.RunAsync(Git.Against(clone, "config", "--get", "--default", string.Empty, OwnerKeyOf(branch)), cancellationToken)).Trim();
            if (!string.Equals(owner, parcelId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                string whose = owner.Length == 0 ? "names no parcel as its owner" : $"belongs to parcel {owner}";
                return (new Checkout.Refused(
                    $"Branch {branch} already exists in the node's clone of project '{project.Project}' and {whose}, so it is not checked out for parcel {parcelId}."), null);
            }
        }

        if (listed.FirstOrDefault(w => !w.Bare && !w.Stale && w.Branch == reference && !RealPath.Same(w.Path, workPath)) is { } holding)
        {
            Guid? holder = FlowRunNamedBy(holding.Path);
            WorktreeHolder? shown = holder is Guid id
                ? await holders.Of(id, cancellationToken)
                : null;

            // A worktree is a flow run's only at the work path the core gives that flow run.
            string? status = shown is { WorkPath: { } holderPath } && RealPath.Same(holding.Path, holderPath)
                ? shown.Status
                : null;
            switch (status)
            {
                case null:
                    return (new Checkout.Refused(
                        $"Branch {branch} is checked out in the worktree at {holding.Path}, which is no work path of a flow run the node holds, so it is not checked out here as well. Remove that worktree from the node's clone at {clone}, and the step checks the branch out."), null);
                case "running" or "paused":
                    return (new Checkout.Refused(
                        $"Branch {branch} is checked out in the worktree of flow run {holder}, which is still live, at {holding.Path}, so it is not checked out here as well. Once that flow run has ended or been stopped, the step takes its worktree over."), null);
            }

            Guid ended = holder.GetValueOrDefault();
            if (!reached.Contains(ended))
            {
                return (null, ended);
            }

            await DiscardAsync(clone, holding, ended, cancellationToken);
            logger.LogInformation(
                "Removed the worktree of flow run {FlowRun}, which has ended or been stopped, at {Path}, so flow run {Taker} can check out branch {Branch}.",
                ShortId.Of(ended), holding.Path, ShortId.Of(flowRunId), branch);
            listed = await ListAsync(clone, cancellationToken);
        }

        string remote = await FetchParcelBranchAsync(clone, branch, cancellationToken);
        string from;
        if (exists)
        {
            from = $"branch {branch}";
            if (remote.Length > 0 && remote != tip)
            {
                switch (await AncestryAsync(clone, tip, remote, cancellationToken))
                {
                    case Ancestry.Behind:
                        await git.RunAsync(Git.Against(clone, "update-ref", reference, remote, tip), cancellationToken);
                        tip = remote;
                        break;
                    case Ancestry.Diverged when string.Equals(remote, lastPushed, StringComparison.Ordinal):
                        // The remote still holds what coanda last pushed, so only the clone's branch
                        // was rewritten, as a person's rebase does; the push replaces the remote's.
                        logger.LogInformation(
                            "Branch {Branch} in the node's clone of project '{Project}' is at {Tip}, rewritten from {Remote}, the commit last recorded as pushed, which the remote still holds; it is checked out as it stands.",
                            branch, project.Project, tip, remote);
                        break;
                    case Ancestry.Diverged:
                        return (new Checkout.Refused(
                            $"Branch {branch} in the node's clone of project '{project.Project}' is at {tip}, and the remote's branch, at {remote}, has commits it does not, while it has commits the remote's does not, and the remote's is not the commit last recorded as pushed for the parcel, so neither can be brought onto the other without someone deciding which to keep. "
                            + $"Reconcile them by hand in {clone}, by moving {reference} to a commit that contains {remote}, or by deleting {reference} there to start again from the remote's branch, and the step checks it out."), null);
                }
            }
        }
        else if (remote.Length > 0)
        {
            from = $"branch {branch} on the remote";
            tip = remote;
        }
        else
        {
            from = $"primary branch {project.PrimaryBranch}";
            tip = await FetchPrimaryAsync(clone, project.PrimaryBranch, cancellationToken);
        }

        // A path that differs from .coanda only in case is the same directory on a filesystem that
        // ignores case, the default on macOS and Windows, and git matches a pathspec's case exactly
        // whatever core.ignorecase says. So the commit's root entries are listed and compared without
        // regard to case, and only those that match are listed again for the paths under them.
        // -z lists each path verbatim, where the default quotes a path holding anything unusual.
        string root = await git.RunAsync(Git.Against(clone, "ls-tree", "-z", "--name-only", tip), cancellationToken);
        string[] matching = [.. root.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(n => string.Equals(n, ".coanda", StringComparison.OrdinalIgnoreCase))];
        string[] underCoanda = [];
        if (matching.Length > 0)
        {
            // A match the commit tracks as a file, a symbolic link or a submodule is listed as itself;
            // a directory, as the files under it.
            string tracked = await git.RunAsync(Git.Against(clone, ["ls-tree", "-r", "-z", "--name-only", tip, "--", .. matching]), cancellationToken);
            string[] paths = tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            underCoanda = paths.Length > 0 ? paths : matching;
        }

        if (underCoanda.Length > 0)
        {
            return (new Checkout.Refused(
                $"Commit {tip} of {from} tracks .coanda, in any letter case, or files under it, where the daemon keeps its own files at a worktree's root, so no worktree is made from it: {string.Join(", ", underCoanda)}."), null);
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
        return (new Checkout.Ready(branch, tip), null);
    }

    /// <summary>The remote-tracking ref the clone keeps for <paramref name="branch"/> on <see cref="Git.Remote"/>.</summary>
    public static string TrackingRefOf(string branch) => $"refs/remotes/{Git.Remote}/{branch}";

    /// <summary>
    /// Pushes the parcel's branch to the project's remote, after making sure the flow run's worktree, at
    /// <paramref name="workPath"/>, has no changes git status reports; a repo with no usable clone pushes
    /// nothing. The push names its lease explicitly, expecting the remote's
    /// branch to hold what the clone's remote-tracking ref for it holds, or not to exist when the
    /// clone holds no such ref. <see cref="CheckOutAsync"/> refreshed that ref from the remote when the
    /// worktree was made, and once the push succeeds it is set to the pushed commit, so the lease
    /// refuses only an update someone made on the remote since. A failing git command other than the push throws
    /// <see cref="GitCommandException"/>.
    /// </summary>
    public async Task<Push> PushAsync(RepoTarget project, Guid parcelId, string workPath, CancellationToken cancellationToken)
    {
        string clone = ClonePathOf(project);
        string branch = BranchOf(parcelId);
        string reference = $"refs/heads/{branch}";
        string tracking = TrackingRefOf(branch);

        using Held held = await LockAsync(clone, cancellationToken);
        if (await UnusableAsync(project, cancellationToken) is { } unusable)
        {
            return new Push.NoClone(unusable);
        }

        string[] changes = await ChangesAsync(workPath, cancellationToken);
        if (changes.Length > 0)
        {
            return new Push.Unclean(changes);
        }

        string head = (await git.RunAsync(Git.Against(clone, "rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"), cancellationToken)).Trim();
        string expect = await RefAsync(clone, tracking, cancellationToken);
        CommandLine push = Git.Against(clone, "push", $"--force-with-lease={reference}:{expect}", Git.Remote, $"{head}:{reference}");
        CommandResult pushed = await git.TryRunAsync(push, cancellationToken);
        if (!pushed.Succeeded)
        {
            string reason = new GitCommandException(push, pushed).Message;
            return pushed.StandardError.Contains("(stale info)", StringComparison.Ordinal)
                ? new Push.Stale(reason)
                : new Push.Failed(reason);
        }

        await git.RunAsync(Git.Against(clone, "update-ref", tracking, head), cancellationToken);
        return new Push.Pushed(head);
    }

    /// <summary>
    /// The ref in the node's clone under which the rebase of <paramref name="flowRunId"/> notes each
    /// commit it is about to push, before the push starts.
    /// </summary>
    public static string NoteRefOf(Guid flowRunId) => $"refs/coanda/rebase/{flowRunId}";

    /// <summary>
    /// Rebases the parcel's branch, in the flow run's worktree at <paramref name="workPath"/>, from <paramref name="recordedHead"/> onto
    /// the project's primary branch as a fetch brings it, and pushes the result to the remote's
    /// branch with a lease that expects <paramref name="recordedHead"/> there. A rebase an
    /// interrupted performance left in progress is aborted first. A worktree with changes, or a
    /// branch whose tip is not the recorded head, is answered before anything is fetched, rebased,
    /// or pushed, unless the remote's branch holds exactly the commit this flow run last noted, and that
    /// is not the recorded head: an earlier performance pushed it and was not recorded, so the branch
    /// is put at it and nothing is pushed. The remote's branch is read without changing a ref.
    /// </summary>
    /// <remarks>
    /// The rebase runs on a detached head started at the recorded head, and the commit it makes is
    /// noted under <see cref="NoteRefOf"/> before the push. The branch moves only once the push has
    /// succeeded, so a failure or a daemon stop at any point leaves it at the recorded head or at the
    /// noted commit the remote already holds. A rebase that stops on a conflict is aborted and the
    /// branch checked out again, as it was before. Every command runs in the clone's one turn at git.
    /// A failing git command other than the rebase and the push throws <see cref="GitCommandException"/>.
    /// </remarks>
    public async Task<Rebase> RebaseAsync(RepoTarget project, Guid parcelId, Guid flowRunId, string workPath, string recordedHead, CancellationToken cancellationToken)
    {
        string clone = ClonePathOf(project);
        string branch = BranchOf(parcelId);
        string reference = $"refs/heads/{branch}";
        string note = NoteRefOf(flowRunId);

        using Held held = await LockAsync(clone, cancellationToken);
        if (await UnusableAsync(project, cancellationToken) is { } unusable)
        {
            return new Rebase.NoClone(unusable);
        }

        CommandLine abort = Git.In(workPath, "rebase", "--abort");
        CommandResult aborted = await git.TryRunAsync(abort, cancellationToken);
        if (!aborted.Succeeded && !aborted.StandardError.Contains("no rebase in progress", StringComparison.OrdinalIgnoreCase))
        {
            throw new GitCommandException(abort, aborted);
        }

        string[] changes = await ChangesAsync(workPath, cancellationToken);
        if (changes.Length > 0)
        {
            return new Rebase.Unclean(changes);
        }

        string remote = await RemoteBranchAsync(clone, reference, cancellationToken);
        string noted = await RefAsync(clone, note, cancellationToken);
        string tip = await RefAsync(clone, reference, cancellationToken);
        if (noted.Length > 0 && remote == noted && noted != recordedHead)
        {
            // An earlier performance pushed the noted commit and was not recorded.
            await SettleAsync(noted, tip);
            return new Rebase.Pushed(noted);
        }

        if (tip != recordedHead)
        {
            return new Rebase.Moved(tip, remote);
        }

        string onto = await FetchPrimaryAsync(clone, project.PrimaryBranch, cancellationToken);
        await git.RunAsync(Git.In(workPath, "checkout", "--quiet", "--detach", recordedHead), cancellationToken);
        // Rebasing a detached head moves no branch; updateRefs would move any that points into it.
        CommandLine rebase = Git.In(workPath, "-c", "rebase.updateRefs=false", "rebase", "--quiet", onto);
        CommandResult rebased = await git.TryRunAsync(rebase, cancellationToken);
        if (!rebased.Succeeded)
        {
            string unmerged = await git.RunAsync(Git.In(workPath, "diff", "--name-only", "--diff-filter=U", "-z"), cancellationToken);
            string[] paths = unmerged.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length == 0)
            {
                throw new GitCommandException(rebase, rebased);
            }

            CommandResult stoppedAt = await git.TryRunAsync(Git.In(workPath, "rev-parse", "--verify", "--quiet", "REBASE_HEAD"), cancellationToken);
            await git.RunAsync(Git.In(workPath, "rebase", "--abort"), cancellationToken);
            await git.RunAsync(Git.In(workPath, "checkout", "--quiet", branch), cancellationToken);
            return new Rebase.Conflict(stoppedAt.Succeeded ? stoppedAt.StandardOutput.Trim() : string.Empty, paths);
        }

        string head = (await git.RunAsync(Git.In(workPath, "rev-parse", "--verify", "--quiet", "HEAD^{commit}"), cancellationToken)).Trim();
        await git.RunAsync(Git.Against(clone, "update-ref", note, head), cancellationToken);
        CommandLine push = Git.Against(clone, "push", $"--force-with-lease={reference}:{recordedHead}", Git.Remote, $"{head}:{reference}");
        CommandResult pushed = await git.TryRunAsync(push, cancellationToken);
        if (!pushed.Succeeded)
        {
            string reason = new GitCommandException(push, pushed).Message;
            return pushed.StandardError.Contains("(stale info)", StringComparison.Ordinal)
                ? new Rebase.Stale(reason)
                : new Rebase.Failed(reason);
        }

        await SettleAsync(head, tip);
        return new Rebase.Pushed(head);

        // Puts the branch at the pushed commit, checked out in the worktree, and the remote-tracking
        // ref with it, as a push by open-pr leaves them. The worktree leaves the branch first, so
        // moving it never moves a checked-out head under its files.
        async Task SettleAsync(string pushedHead, string from)
        {
            await git.RunAsync(Git.In(workPath, "checkout", "--quiet", "--detach", pushedHead), cancellationToken);
            if (from != pushedHead)
            {
                await git.RunAsync(Git.Against(clone, "update-ref", reference, pushedHead, from), cancellationToken);
            }

            await git.RunAsync(Git.In(workPath, "checkout", "--quiet", branch), cancellationToken);
            await git.RunAsync(Git.Against(clone, "update-ref", TrackingRefOf(branch), pushedHead), cancellationToken);
        }
    }

    /// <summary>
    /// Removes the worktree at <paramref name="workPath"/>, the work path the core gives the flow run
    /// <paramref name="flowRunId"/>, from whichever of the project's clones lists it, any uncommitted
    /// changes in it included, logging the paths of the changes it discards; the branch and its commits
    /// stay in the clone. A project with no clones, or whose clones do not list the path, is left alone.
    /// Returns whether a worktree was removed. A failing git command in one clone does not keep the others
    /// from being looked at. Once they all have been, a failure is logged and passed over when the worktree
    /// was removed from another clone, since a path is one clone's worktree at most; otherwise the first
    /// failure is thrown as the <see cref="GitCommandException"/> it was, because the worktree may be in
    /// the clone that failed.
    /// </summary>
    public async Task<bool> RemoveAsync(string project, Guid flowRunId, string workPath, CancellationToken cancellationToken)
    {
        bool removed = false;
        GitCommandException? failed = null;
        foreach (string clone in ClonesOf(project))
        {
            try
            {
                using Held held = await LockAsync(clone, cancellationToken);
                ListedWorktree? entry = (await ListAsync(clone, cancellationToken)).FirstOrDefault(w => !w.Bare && RealPath.Same(w.Path, workPath));
                if (entry is not null)
                {
                    await DiscardAsync(clone, entry, flowRunId, cancellationToken);
                    removed = true;
                }
            }
            catch (GitCommandException failure)
            {
                failed ??= failure;
            }
        }

        if (failed is not null && !removed)
        {
            ExceptionDispatchInfo.Throw(failed);
        }

        if (failed is not null)
        {
            logger.LogWarning(failed, "A clone of project '{Project}' could not be looked at while removing the worktree of flow run {FlowRun}, which was removed from another clone.", project, ShortId.Of(flowRunId));
        }

        return removed;
    }

    /// <summary>
    /// Removes, as <see cref="RemoveAsync"/> does, every worktree a clone under the node home lists whose
    /// path is the work path the core gives a flow run that has ended or been stopped, which
    /// <paramref name="finalWorkPath"/> answers with, given the project and the flow run the path's last
    /// segment names, and answers null for a flow run that is live or that the core does not hold. A
    /// listed path that is not that flow run's work path, the clone's own included, is left alone. A
    /// clone, or a worktree, that cannot be swept is logged and passed over.
    /// </summary>
    public async Task SweepAsync(Func<string, Guid, CancellationToken, Task<string?>> finalWorkPath, CancellationToken cancellationToken)
    {
        string projects = RepoLayout.ProjectsPath(homePath);
        if (!Directory.Exists(projects))
        {
            return;
        }

        foreach (string project in Directory.EnumerateDirectories(projects).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
        {
            foreach (string clone in ClonesOf(project))
            {
                try
                {
                    await SweepCloneAsync(project, clone, finalWorkPath, cancellationToken);
                }
                catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogError(failure, "Sweeping the worktrees of the clone at {Clone} failed; they are left as they are.", clone);
                }
            }
        }
    }

    private async Task SweepCloneAsync(string project, string clone, Func<string, Guid, CancellationToken, Task<string?>> finalWorkPath, CancellationToken cancellationToken)
    {
        using Held held = await LockAsync(clone, cancellationToken);
        foreach (ListedWorktree entry in await ListAsync(clone, cancellationToken))
        {
            if (entry.Bare
                || FlowRunNamedBy(entry.Path) is not Guid flowRunId
                || await finalWorkPath(project, flowRunId, cancellationToken) is not { } workPath
                || !RealPath.Same(entry.Path, workPath))
            {
                continue;
            }

            try
            {
                await DiscardAsync(clone, entry, flowRunId, cancellationToken);
                logger.LogInformation("Removed the worktree of flow run {FlowRun}, which has ended or been stopped, at {Path}.", ShortId.Of(flowRunId), entry.Path);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(failure, "Removing the worktree of flow run {FlowRun} at {Path} failed; it is left as it is.", ShortId.Of(flowRunId), entry.Path);
            }
        }
    }

    /// <summary>
    /// The clones the core keeps of <paramref name="project"/>'s repos, by the layout: every directory in
    /// its <c>repos</c> directory but one a clone being made occupies, whose name starts with a dot. A
    /// project whose name is not a plain directory name has none.
    /// </summary>
    private IEnumerable<string> ClonesOf(string project)
    {
        if (!RepoLayout.IsPlain(project))
        {
            return [];
        }

        string repos = RepoLayout.ReposPath(homePath, project);
        return Directory.Exists(repos)
            ? [.. Directory.EnumerateDirectories(repos).Where(d => !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// The flow run whose id is the last segment of <paramref name="path"/>, or null when it names none. It
    /// is that flow run's worktree only when the path is also the work path the core gives it, which the
    /// caller checks.
    /// </summary>
    private static Guid? FlowRunNamedBy(string path) =>
        Guid.TryParse(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), out Guid flowRunId)
            ? flowRunId
            : null;

    /// <summary>
    /// Why the module cannot use the core's clone of the repo <paramref name="target"/> acts on, or null
    /// when it can. The module makes no clone: one that is missing, or something git does not recognize
    /// as a repository at its path, is the reason, which names the path and what puts a clone there.
    /// </summary>
    public async Task<string?> UnusableAsync(RepoTarget target, CancellationToken cancellationToken)
    {
        string clone = ClonePathOf(target);
        if (!Directory.Exists(clone) && !File.Exists(clone))
        {
            return $"the node holds no clone of repo '{target.Repo}' of project '{target.Project}' at {clone}, where the daemon makes it; the next daemon start makes it there.";
        }

        CommandResult check = await git.TryRunAsync(Git.Against(clone, "rev-parse", "--is-bare-repository"), cancellationToken);
        return check.Succeeded
            ? null
            : $"{clone} is where the node keeps its clone of repo '{target.Repo}' of project '{target.Project}', and git does not recognize what is there as a repository; move it aside, and the next daemon start makes the clone there.";
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

    /// <summary>
    /// Brings the remote's branch <paramref name="branch"/> into the clone's remote-tracking ref for
    /// it and returns the commit it holds, or deletes that ref and returns an empty string when the
    /// remote has no such branch, so the push's lease expects what the remote holds now. A fetch that
    /// fails any other way throws <see cref="GitCommandException"/>.
    /// </summary>
    private async Task<string> FetchParcelBranchAsync(string clone, string branch, CancellationToken cancellationToken)
    {
        string tracking = TrackingRefOf(branch);
        CommandLine fetch = Git.Against(clone, "fetch", "--no-tags", "--quiet", Git.Remote, $"+refs/heads/{branch}:{tracking}");
        CommandResult fetched = await git.TryRunAsync(fetch, cancellationToken);
        if (!fetched.Succeeded && fetched.StandardError.Contains("couldn't find remote ref", StringComparison.Ordinal))
        {
            await git.RunAsync(Git.Against(clone, "update-ref", "-d", tracking), cancellationToken);
            return string.Empty;
        }

        if (fetched is not { Succeeded: true, Truncated: false })
        {
            throw new GitCommandException(fetch, fetched);
        }

        return await RefAsync(clone, tracking, cancellationToken);
    }

    /// <summary>
    /// Brings the project's primary branch into the clone's remote-tracking ref for it and returns
    /// the commit it holds.
    /// </summary>
    private async Task<string> FetchPrimaryAsync(string clone, string primaryBranch, CancellationToken cancellationToken)
    {
        await git.RunAsync(
            Git.Against(clone, "fetch", "--no-tags", "--quiet", Git.Remote, $"+refs/heads/{primaryBranch}:refs/remotes/{Git.Remote}/{primaryBranch}"),
            cancellationToken);
        return (await git.RunAsync(
            Git.Against(clone, "rev-parse", "--verify", "--quiet", $"refs/remotes/{Git.Remote}/{primaryBranch}^{{commit}}"),
            cancellationToken)).Trim();
    }

    /// <summary>The commit the remote's <paramref name="reference"/> holds, read without changing a ref, or an empty string when it has none.</summary>
    private async Task<string> RemoteBranchAsync(string clone, string reference, CancellationToken cancellationToken)
    {
        string listed = await git.RunAsync(Git.Against(clone, "ls-remote", "--refs", Git.Remote, reference), cancellationToken);
        return Lines(listed)
            .Select(l => l.Split('\t', 2))
            .Where(parts => parts is [_, string named] && named == reference)
            .Select(parts => parts[0].Trim())
            .FirstOrDefault() ?? string.Empty;
    }

    /// <summary>The commit <paramref name="reference"/> holds in the clone, or an empty string when it holds none.</summary>
    private async Task<string> RefAsync(string clone, string reference, CancellationToken cancellationToken) =>
        (await git.RunAsync(Git.Against(clone, "for-each-ref", "--format=%(objectname)", reference), cancellationToken)).Trim();

    /// <summary>The paths of the changes git status reports in the worktree at <paramref name="workPath"/>.</summary>
    private async Task<string[]> ChangesAsync(string workPath, CancellationToken cancellationToken)
    {
        string status = await git.RunAsync(Git.In(workPath, "status", "--porcelain", "--untracked-files=all"), cancellationToken);
        return [.. Lines(status).Select(l => l.Length > 3
            ? l[3..]
            : l)];
    }

    /// <summary>Where the commit <paramref name="local"/> stands against <paramref name="remote"/>, which differs from it.</summary>
    private async Task<Ancestry> AncestryAsync(string clone, string local, string remote, CancellationToken cancellationToken) =>
        await IsAncestorAsync(clone, local, remote, cancellationToken)
            ? Ancestry.Behind
            : await IsAncestorAsync(clone, remote, local, cancellationToken)
                ? Ancestry.Ahead
                : Ancestry.Diverged;

    private async Task<bool> IsAncestorAsync(string clone, string ancestor, string descendant, CancellationToken cancellationToken)
    {
        CommandLine check = Git.Against(clone, "merge-base", "--is-ancestor", ancestor, descendant);
        CommandResult result = await git.TryRunAsync(check, cancellationToken);
        return result switch
        {
            { Succeeded: true } => true,
            { ExitCode: 1, TimedOut: false } => false,
            _ => throw new GitCommandException(check, result),
        };
    }

    /// <summary>Where a local branch's commit stands against the remote's branch of the same name.</summary>
    private enum Ancestry
    {
        /// <summary>The remote's commit contains the local one, so the local branch moves forward to it.</summary>
        Behind,

        /// <summary>The local commit contains the remote's, so a push only adds to it.</summary>
        Ahead,

        /// <summary>Each has commits the other does not.</summary>
        Diverged,
    }

    private async Task<IReadOnlyList<ListedWorktree>> ListAsync(string clone, CancellationToken cancellationToken) =>
        ListedWorktree.Parse(await git.RunAsync(Git.Against(clone, "worktree", "list", "--porcelain"), cancellationToken));

    /// <summary>Removes a listed worktree after logging the paths of the uncommitted changes in it that the removal discards.</summary>
    private async Task DiscardAsync(string clone, ListedWorktree entry, Guid flowRunId, CancellationToken cancellationToken)
    {
        if (!entry.Prunable && Directory.Exists(entry.Path))
        {
            CommandResult status = await git.TryRunAsync(Git.In(entry.Path, "status", "--porcelain", "--untracked-files=all"), cancellationToken);
            string[] changed = status.Succeeded ? [.. Lines(status.StandardOutput).Select(l => l.Length > 3 ? l[3..] : l)] : [];
            if (!status.Succeeded)
            {
                logger.LogWarning(
                    "The uncommitted changes in the worktree of flow run {FlowRun} at {Path} could not be listed (git status exited with code {Code}); the removal discards them unlisted.",
                    ShortId.Of(flowRunId), entry.Path, status.ExitCode);
            }
            else if (status.Truncated)
            {
                logger.LogWarning(
                    "Removing the worktree of flow run {FlowRun} at {Path} discards more uncommitted changes than can be listed; the last of them are to: {Changes}",
                    ShortId.Of(flowRunId), entry.Path, string.Join(", ", changed));
            }
            else if (changed.Length > 0)
            {
                logger.LogWarning(
                    "Removing the worktree of flow run {FlowRun} at {Path} discards uncommitted changes to: {Changes}",
                    ShortId.Of(flowRunId), entry.Path, string.Join(", ", changed));
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
