namespace Coanda.Core.Registry;

/// <summary>
/// The five system steps the binary shipped without a module prefix before the software module was
/// carved out of it: worktree, gates, open-pr, gated-merge, and rebase. A flow run keeps the flow version
/// it started under, so a flow run that started before the change names them, and its steps record them
/// as their performer, without the prefix. Exactly these five are read as the software module's steps
/// (<see cref="Canonical"/>); any other unprefixed name is no module's.
/// </summary>
public static class UnprefixedSteps
{
    /// <summary>The module the five steps are read as belonging to.</summary>
    public const string Module = "software";

    /// <summary>The five names.</summary>
    public static readonly IReadOnlyList<string> Names = ["worktree", "gates", "open-pr", "gated-merge", "rebase"];

    /// <summary>
    /// The prefixed name of <paramref name="systemStep"/> when it is one of the five <see cref="Names"/>,
    /// and <paramref name="systemStep"/> as it is otherwise.
    /// </summary>
    public static string Canonical(string systemStep) =>
        Names.Contains(systemStep, StringComparer.Ordinal)
            ? $"{Module}/{systemStep}"
            : systemStep;
}
