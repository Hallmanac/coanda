using Coanda.Core.Agents;
using Coanda.Core.Flights;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Runs one judgment through an executor: spawn once, collect, and validate the answer against the
/// spawn's answer schema. A missing or invalid answer is retried once by resuming the same
/// session, never by a second spawn; a second failure parks with the transcript as the lever. It
/// records nothing and leaves the session open: the runner records the flight's events and
/// decides when a session ends.
/// </summary>
public static class Judgment
{
    public const string NoAnswerFound = "no answer document was found";

    public static async Task<JudgmentOutcome> RunAsync(
        IAgentExecutor executor,
        JudgmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(request);

        SessionHandle handle = await executor.SpawnAsync(
            request.Agent,
            request.Context,
            request.WorkingDirectory,
            request.Caps,
            request.AnswerSchema,
            request.CredentialProfile,
            cancellationToken);

        CollectedAnswer first = await executor.CollectAsync(handle, cancellationToken);
        IReadOnlyList<string> firstFailures = Check(request.AnswerSchema, first.Answer);
        if (first.Answer is { } firstAnswer && firstFailures.Count == 0)
        {
            return new JudgmentOutcome.Answered(firstAnswer, first.TranscriptPath, first.Usage);
        }

        await executor.ResumeAsync(handle, RetryMessage(request.AnswerSchema, first.Answer, firstFailures), cancellationToken);

        CollectedAnswer second = await executor.CollectAsync(handle, cancellationToken);
        Measurement usage = Sum(first.Usage, second.Usage);
        IReadOnlyList<string> secondFailures = Check(request.AnswerSchema, second.Answer);
        return second.Answer is { } secondAnswer && secondFailures.Count == 0
            ? new JudgmentOutcome.Answered(secondAnswer, second.TranscriptPath, usage)
            : new JudgmentOutcome.Parked(
                $"Agent '{request.Agent.Name}' gave no valid '{request.AnswerSchema}' answer after one retry: {string.Join("; ", secondFailures)}.",
                second.TranscriptPath,
                usage);
    }

    /// <summary>The validator's failures for the answer, or the one failure of there being no answer.</summary>
    private static IReadOnlyList<string> Check(string schema, string? answer) => answer is null
        ? [NoAnswerFound]
        : AnswerValidator.Validate(schema, answer).Failures;

    private static string RetryMessage(string schema, string? answer, IReadOnlyList<string> failures) => answer is null
        ? $"No answer document was found in your reply. End your reply with exactly one JSON document that matches the '{schema}' answer schema."
        : $"Your answer did not match the '{schema}' answer schema. The validator reported: {string.Join("; ", failures)}. "
            + $"End your reply with exactly one JSON document that matches the '{schema}' answer schema.";

    private static Measurement Sum(Measurement first, Measurement second) => new(
        first.Tokens + second.Tokens,
        first.CostUsd + second.CostUsd,
        first.Turns + second.Turns,
        first.Duration + second.Duration);
}
