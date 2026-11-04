using Coanda.Core.Projects;
using Coanda.Core.Software;
using Coanda.Daemon.Commands;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Projects;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Runs the project's build command and then its test command, each as one command line through the
/// platform's shell in the flow run's work path, stopping at the first that exits nonzero. Both passing
/// answers done; one failing, the shell's own exit for a command it cannot find included, answers
/// failed with a <c>reason</c> naming the command and its exit code and carrying at most the last
/// <see cref="QuotedLines"/> lines of its output, so the contour routes to its fix step. A command
/// still running after <paramref name="commandLimit"/> is ended and answers failed the same way. A
/// shell that cannot be started, or a project with no record, pauses with the perform-again lever instead.
/// </summary>
public sealed class GatesAction(ProjectService projects, ICommandRunner runner, NodeHome home, TimeSpan commandLimit) : IFlowAction
{
    /// <summary>How many lines of a failing command's output its reason carries.</summary>
    public const int QuotedLines = 100;

    public string Name => SoftwareActions.Gates;

    public async ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        Project? project = await projects.ShowAsync(request.Principal, request.Project, cancellationToken);
        if (project is null)
        {
            return new ActionOutcome.Paused(
                $"Step '{request.Step.Name}' has no build or test command to run for project '{request.Project}': {ProjectService.Unregistered(request.Project)}",
                FlowRunLevers.PerformAgain(request.Step.Name));
        }

        string workPath = home.FlowRunWorkPath(request.Project, request.FlowRunId);
        foreach ((string kind, string command) in new[] { ("build", project.Build), ("test", project.Test) })
        {
            CommandResult result;
            try
            {
                result = await runner.RunAsync(CommandLine.Shell(command, workPath) with { Limit = commandLimit }, cancellationToken);
            }
            catch (CommandNotStartedException unstarted)
            {
                return new ActionOutcome.Paused(
                    $"Step '{request.Step.Name}' could not start the shell to run the {kind} command '{command}': {unstarted.Message}",
                    FlowRunLevers.PerformAgain(request.Step.Name));
            }

            if (!result.Succeeded)
            {
                return ActionOutcome.FailedWith(Name, Reason(kind, command, result, commandLimit));
            }
        }

        return ActionOutcome.DoneWith(Name, ("build", project.Build), ("test", project.Test));
    }

    private static string Reason(string kind, string command, CommandResult result, TimeSpan limit)
    {
        IReadOnlyList<string> lines = OutputText.LastLines(result.Combined, QuotedLines);
        string output = lines.Count == 0
            ? "It wrote no output."
            : $"The last {lines.Count} lines of its output:\n{string.Join('\n', lines)}";
        string ended = result.TimedOut
            ? $"was still running after {Durations.Describe(limit)}, the limit for one gates command, and was ended"
            : $"exited with code {result.ExitCode}";
        return OutputText.Redact($"The {kind} command '{command}' {ended}. {output}");
    }
}
