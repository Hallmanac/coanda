namespace Coanda.Daemon.GitHub;

/// <summary>
/// A pull request as a merge inspects it. <see cref="State"/> is GitHub's <c>open</c> or
/// <c>closed</c>; <see cref="MergeCommit"/> is the commit GitHub reports for a merge once
/// <see cref="Merged"/>; <see cref="Mergeable"/> is null while GitHub has not yet computed it.
/// </summary>
public sealed record GitHubPullRequestState(
    int Number, string State, bool Merged, string? MergeCommit, bool? Mergeable, string BaseRef, string HeadSha)
{
    public bool IsOpen => string.Equals(State, "open", StringComparison.Ordinal);
}

/// <summary>One check run reported on a commit: its name, <c>status</c> and, once completed, its <c>conclusion</c>. Later runs carry greater ids.</summary>
public sealed record GitHubCheckRun(long Id, string Name, string Status, string? Conclusion)
{
    public bool IsCompleted => string.Equals(Status, "completed", StringComparison.Ordinal);
}

/// <summary>One commit status posted on a commit: its context and its <c>state</c>. Later statuses carry greater ids.</summary>
public sealed record GitHubCommitStatus(long Id, string Context, string State);

/// <summary>The merge methods a repository allows.</summary>
public sealed record GitHubMergeMethods(bool Merge, bool Squash, bool Rebase)
{
    /// <summary>The method a merge names: merge, then squash, then rebase; null when none is allowed.</summary>
    public string? Preferred => this switch
    {
        { Merge: true } => "merge",
        { Squash: true } => "squash",
        { Rebase: true } => "rebase",
        _ => null,
    };
}
