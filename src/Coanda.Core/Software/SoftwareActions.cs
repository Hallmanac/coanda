namespace Coanda.Core.Software;

/// <summary>
/// The names of the software engineering actions: worktree, gates, pull request, merge, rebase.
/// Nothing in the flow shape or its validation refers to this type; the daemon passes these names
/// in alongside the core ones, so the meta-model stays free of software.
/// </summary>
public static class SoftwareActions
{
    public const string Worktree = "worktree";
    public const string Gates = "gates";
    public const string OpenPr = "open-pr";
    public const string GatedMerge = "gated-merge";
    public const string Rebase = "rebase";

    public static readonly IReadOnlyList<string> All = [Worktree, Gates, OpenPr, GatedMerge, Rebase];
}
