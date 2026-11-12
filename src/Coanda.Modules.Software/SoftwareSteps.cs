namespace Coanda.Modules.Software;

/// <summary>
/// The names of the software engineering steps, each prefixed with the module's name as the module API
/// requires: worktree, gates, review diff, pull request, merge, rebase. Flows written before step names
/// were prefixed name five of them, all but the review diff, without the prefix, and a flow run keeps the
/// flow version it started under, so <see cref="Canonical"/> reads exactly those five unprefixed names as
/// the module's. The review diff came after the prefix, so no stored flow names it unprefixed.
/// </summary>
public static class SoftwareSteps
{
    /// <summary>The module's name, which prefixes its steps.</summary>
    public const string Module = "software";

    public const string Worktree = "software/worktree";
    public const string Gates = "software/gates";
    public const string ReviewDiff = "software/review-diff";
    public const string OpenPr = "software/open-pr";
    public const string GatedMerge = "software/gated-merge";
    public const string Rebase = "software/rebase";

    public static readonly IReadOnlyList<string> All = [Worktree, Gates, ReviewDiff, OpenPr, GatedMerge, Rebase];

    /// <summary>The names the five steps shipped under before they were prefixed, which stored flow versions still name.</summary>
    public static readonly IReadOnlyList<string> Unprefixed = ["worktree", "gates", "open-pr", "gated-merge", "rebase"];

    /// <summary>
    /// The prefixed name of <paramref name="step"/> when it is one of the five <see cref="Unprefixed"/>
    /// names, and <paramref name="step"/> as it is otherwise.
    /// </summary>
    public static string Canonical(string step) =>
        Unprefixed.Contains(step, StringComparer.Ordinal)
            ? $"{Module}/{step}"
            : step;
}
