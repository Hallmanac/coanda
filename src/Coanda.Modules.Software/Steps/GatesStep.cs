using System.Text.Json;
using Coanda.Sdk;
using Coanda.Sdk.Client;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// Runs the build command and then the test command the module's settings name in the project, their
/// defaults included, each as one command line through the
/// platform's shell in the flow run's work path, the one the dispatch carries, stopping at the first that exits nonzero. Both passing
/// answers done; one failing, the shell's own exit for a command it cannot find included, answers
/// failed with a <c>reason</c> naming the command and its exit code and carrying at most the last
/// <see cref="QuotedLines"/> lines of its output, so the flow routes to its fix step. A command
/// still running after <paramref name="commandLimit"/> is ended and answers failed the same way. A
/// shell that cannot be started, or a project the core shows no record of, pauses with the perform-again
/// lever instead, and a command with no value and no default pauses with a lever naming its setting.
/// </summary>
public sealed class GatesStep(ICommandRunner runner, TimeSpan commandLimit) : ISoftwareStep
{
    /// <summary>How many lines of a failing command's output its reason carries.</summary>
    public const int QuotedLines = 100;

    public string Name => SoftwareSteps.Gates;

    public async Task<DispatchAnswer> DispatchAsync(StepContext context, CancellationToken cancellationToken)
    {
        DispatchRequest request = context.Request;
        CoreModuleSettings settings;
        try
        {
            settings = await context.SettingsAsync(cancellationToken);
        }
        catch (CoreApiException missing)
        {
            return await context.PerformAgainAsync(
                $"has no build or test command to run for project '{request.Project}': {missing.Message}", cancellationToken);
        }

        if (Command(settings, SoftwareSettings.Build) is not { } build)
        {
            return await MissingAsync(SoftwareSettings.Build, $"'{SoftwareSettings.DefaultBuild}'");
        }

        if (Command(settings, SoftwareSettings.Test) is not { } test)
        {
            return await MissingAsync(SoftwareSettings.Test, $"'{SoftwareSettings.DefaultTest}'");
        }

        foreach ((string kind, string command) in new[] { ("build", build), ("test", test) })
        {
            CommandResult result;
            try
            {
                result = await runner.RunAsync(CommandLine.Shell(command, request.WorkPath) with { Limit = commandLimit }, cancellationToken);
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

        return Results.DoneWith(Name, ("build", build), ("test", test));

        Task<DispatchAnswer> MissingAsync(string setting, string example) =>
            StepTargets.MissingSettingAsync(
                context,
                setting,
                $"has no {setting} command to run: the software module's setting '{setting}' has no value and no default in project '{request.Project}'.",
                example,
                cancellationToken);
    }

    /// <summary>The command the setting <paramref name="name"/> names, its default included, or null when it names none.</summary>
    private static string? Command(CoreModuleSettings settings, string name) =>
        settings.ValueOf(name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { } command && !string.IsNullOrWhiteSpace(command)
            ? command
            : null;

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
