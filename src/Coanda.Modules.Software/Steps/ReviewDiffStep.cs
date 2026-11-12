using Coanda.Modules.Software.Repositories;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// Writes the change under review to <see cref="ReviewDiffFile.RelativePath"/> in the flow run's work
/// path, for the reviewer agents to read: the diff from the merge base of the worktree's head and the
/// primary branch of the repo the task works in (<see cref="StepTargets.TargetAsync"/>), fetched first,
/// to that head (<see cref="ProjectClones.ReviewDiffAsync"/>). It answers done with the <c>base</c> and
/// <c>head</c> it diffed. A diff that cannot be produced, the primary branch failing to fetch among the
/// reasons, or that cannot be written, still answers done, with a <c>reason</c> saying no diff was
/// written and why, and leaves no file at that path, so a file an earlier pass wrote never stands for
/// this one; the flow goes on to its reviewers, who are told what to do when the file is absent. A
/// cancelled dispatch propagates its cancellation. A repo or primary branch that cannot be found pauses
/// with the lever <see cref="StepTargets.TargetAsync"/> gives, a project the core shows no record of
/// pauses with the perform-again lever, and so does a file left at that path that cannot be removed.
/// </summary>
public sealed class ReviewDiffStep(ProjectClones clones) : ISoftwareStep
{
    public string Name => SoftwareSteps.ReviewDiff;

    public async Task<DispatchAnswer> DispatchAsync(StepContext context, CancellationToken cancellationToken)
    {
        DispatchRequest request = context.Request;
        CoreProject project;
        try
        {
            project = await context.Core.ProjectAsync(request.Project, cancellationToken);
        }
        catch (CoreApiException missing)
        {
            return await context.PerformAgainAsync($"has no repository to diff for review: {missing.Message}", cancellationToken);
        }

        if ((await StepTargets.TargetAsync(context, project, cancellationToken)).Lacks(out RepoTarget? target, out DispatchAnswer? pause))
        {
            return pause;
        }

        if (Removed(request.WorkPath) is { } stuck)
        {
            return await context.PerformAgainAsync(
                $"could not remove the {ReviewDiffFile.RelativePath} an earlier pass left in the work path, so the reviewers could read it as this pass's change: {stuck}",
                cancellationToken);
        }

        return await clones.ReviewDiffAsync(target, request.WorkPath, cancellationToken) switch
        {
            ReviewDiff.Produced produced => await WriteAsync(context, produced, cancellationToken),
            ReviewDiff.Unproduced unproduced => NotWritten(unproduced.Reason),
            ReviewDiff other => throw new InvalidOperationException($"A review diff ended in a way the review diff step does not know: {other.GetType().Name}."),
        };
    }

    /// <summary>Writes the diff, answering done with its base and head, or, when it cannot be written, done saying so once nothing is left at its path.</summary>
    private async Task<DispatchAnswer> WriteAsync(StepContext context, ReviewDiff.Produced produced, CancellationToken cancellationToken)
    {
        string workPath = context.Request.WorkPath;
        try
        {
            ReviewDiffFile.Write(workPath, produced.Text);
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            return Removed(workPath) is { } left
                ? await context.PerformAgainAsync(
                    $"could not write {ReviewDiffFile.RelativePath} ({unwritten.Message}), and could not remove what the write left there either: {left}",
                    cancellationToken)
                : NotWritten($"it could not be written: {unwritten.Message}");
        }

        return Results.DoneWith(Name, ("base", produced.Base), ("head", produced.Head));
    }

    /// <summary>A done answer saying no diff was written, for <paramref name="why"/>.</summary>
    private DispatchAnswer.Done NotWritten(string why) =>
        Results.DoneWith(Name, ("reason", $"Wrote no {ReviewDiffFile.RelativePath}: {why}"));

    /// <summary>Removes the file from the work path, returning null once it is gone, or why it could not be removed.</summary>
    private static string? Removed(string workPath)
    {
        try
        {
            ReviewDiffFile.Remove(workPath);
            return null;
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            return failed.Message;
        }
    }
}
