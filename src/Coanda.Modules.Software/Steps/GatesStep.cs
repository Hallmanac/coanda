using Coanda.Modules.Software.Repositories;
using Coanda.Sdk;
using Coanda.Sdk.Client;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// Runs the project's build command and then its test command, each as one command line through the
/// platform's shell in the flow run's work path, stopping at the first that exits nonzero. Both passing
/// answers done; one failing, the shell's own exit for a command it cannot find included, answers
/// failed with a <c>reason</c> naming the command and its exit code and carrying at most the last
/// <see cref="QuotedLines"/> lines of its output, so the flow routes to its fix step. A command
/// still running after <paramref name="commandLimit"/> is ended and answers failed the same way. A
/// shell that cannot be started, or a project the core shows no record of, pauses with the perform-again
/// lever instead.
/// </summary>
public sealed class GatesStep(ClonePaths paths, ICommandRunner runner, TimeSpan commandLimit) : ISoftwareStep
{
    /// <summary>How many lines of a failing command's output its reason carries.</summary>
    public const int QuotedLines = 100;

    public string Name => SoftwareSteps.Gates;

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
            return await context.PerformAgainAsync(
                $"has no build or test command to run for project '{request.Project}': {missing.Message}", cancellationToken);
        }

        string workPath = paths.FlowRunWorkPath(request.Project, request.FlowRunId);
        foreach ((string kind, string command) in new[] { ("build", project.Build), ("test", project.Test) })
        {
            CommandResult result;
            try
            {
                result = await runner.RunAsync(CommandLine.Shell(command, workPath) with { Limit = commandLimit }, cancellationToken);
            }
            catch (CommandNotStartedException unstarted)
            {
                return await context.PerformAgainAsync(
                    $"could not start the shell to run the {kind} command '{command}': {unstarted.Message}", cancellationToken);
            }

            if (!result.Succeeded)
            {
                return Results.FailedWith(Name, Reason(kind, command, result, commandLimit));
            }
        }

        return Results.DoneWith(Name, ("build", project.Build), ("test", project.Test));
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
