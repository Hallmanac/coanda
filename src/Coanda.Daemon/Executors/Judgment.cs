using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flights;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Runs one judgment through an executor: spawn once, collect, and validate the answer against the
/// spawn's answer schema. A missing or invalid answer is retried once by resuming the same
/// session, never by a second spawn; a second failure parks with the transcript as the lever. It
/// records nothing and leaves the session open on either outcome, returning its handle: the runner
/// records the flight's events and decides when a session ends. If the judgment fails after the
/// spawn, it stops the session rather than leave one nobody holds, except when the caller already
/// took the handle and the failure is a cancellation, which leaves the session for a reattach.
/// </summary>
public static class Judgment
{
    public const string NoAnswerFound = "no answer document was found";

    public static Task<JudgmentOutcome> RunAsync(
        IAgentExecutor executor,
        JudgmentRequest request,
        CancellationToken cancellationToken) => RunAsync(executor, request, onSpawned: null, cancellationToken);

    /// <summary>
    /// Runs the judgment, handing the session's handle to <paramref name="onSpawned"/> as soon as the
    /// spawn returns, so the runner can record it before the first collect.
    /// </summary>
    public static async Task<JudgmentOutcome> RunAsync(
        IAgentExecutor executor,
        JudgmentRequest request,
        Func<SessionHandle, CancellationToken, ValueTask>? onSpawned,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(request);
        RequireSchema(request);

        SessionHandle handle = await executor.SpawnAsync(
            request.Agent,
            request.Context,
            request.WorkingDirectory,
            request.Caps,
            request.AnswerSchema,
            request.CredentialProfile,
            cancellationToken);

        bool handedOver = false;
        try
        {
            if (onSpawned is not null)
            {
                await onSpawned(handle, cancellationToken);
                handedOver = true;
            }

            return await AnswerAsync(executor, request, handle, cancellationToken);
        }
        catch (Exception failure) when (!(handedOver && failure is OperationCanceledException))
        {
            await StopAfterFailureAsync(executor, handle, failure);
            throw;
        }
    }

    private static async Task<JudgmentOutcome> AnswerAsync(
        IAgentExecutor executor,
        JudgmentRequest request,
        SessionHandle handle,
        CancellationToken cancellationToken)
    {
        CollectedAnswer first = await executor.CollectAsync(handle, cancellationToken);
        IReadOnlyList<string> firstFailures = Check(request.AnswerSchema, first.Answer);
        if (first.Answer is { } firstAnswer && firstFailures.Count == 0)
        {
            return new JudgmentOutcome.Answered(handle, firstAnswer, first.TranscriptPath, first.Usage);
        }

        await executor.ResumeAsync(handle, RetryMessage(request.AnswerSchema, first.Answer, firstFailures), cancellationToken);

        CollectedAnswer second = await executor.CollectAsync(handle, cancellationToken);
        Measurement usage = Sum(first.Usage, second.Usage);
        IReadOnlyList<string> secondFailures = Check(request.AnswerSchema, second.Answer);
        return second.Answer is { } secondAnswer && secondFailures.Count == 0
            ? new JudgmentOutcome.Answered(handle, secondAnswer, second.TranscriptPath, usage)
            : new JudgmentOutcome.Parked(
                handle,
                $"Agent '{request.Agent.Name}' gave no valid '{request.AnswerSchema}' answer after one retry: {string.Join("; ", secondFailures)}.",
                second.TranscriptPath,
                usage);
    }

    /// <summary>
    /// Refuses, before anything is spawned, a schema the catalog does not hold or one that differs
    /// from the agent's own, since the agent is told to answer in its own schema.
    /// </summary>
    private static void RequireSchema(JudgmentRequest request)
    {
        AnswerSchemas.Require(request.AnswerSchema);
        if (!string.Equals(request.AnswerSchema, request.Agent.AnswerSchema, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"The judgment names the answer schema '{request.AnswerSchema}', but agent '{request.Agent.Name}' answers in '{request.Agent.AnswerSchema}'.");
        }
    }

    /// <summary>
    /// Stops a session the judgment can no longer hand back. The stop runs uncancelled, since the
    /// failure may itself be a cancellation; a stop that also fails is reported alongside the failure.
    /// </summary>
    private static async Task StopAfterFailureAsync(IAgentExecutor executor, SessionHandle handle, Exception failure)
    {
        try
        {
            await executor.StopAsync(handle, CancellationToken.None);
        }
        catch (Exception stopFailure)
        {
            throw new AggregateException(
                $"The judgment failed and stopping session {handle.Id} for agent '{handle.Agent}' failed too.",
                failure,
                stopFailure);
        }
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
