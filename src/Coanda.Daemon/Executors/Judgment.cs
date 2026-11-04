using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Runs one judgment through an executor: spawn once, collect, and validate the answer against the
/// spawn's answer schema. A missing or invalid answer is retried once by resuming the same
/// session, never by a second spawn; a second failure pauses with the transcript as the lever. It
/// records nothing and leaves the session open on either outcome, returning its handle: the runner
/// records the flow run's events and decides when a session ends. If the judgment fails after the
/// spawn, it stops the session rather than leave one nobody holds, except when the caller already
/// took the handle and its stopping token (the daemon's own) is cancelled, which leaves the session
/// for a reattach. Whether a failure is a daemon stop is decided by that token alone, never by the
/// failure's type: an executor that throws a cancellation of its own accord has its session stopped.
/// <see cref="ReattachAsync"/> runs the same collect, validation, and retry on a session a daemon
/// restart found still held, starting from where that session stands instead of a spawn.
/// </summary>
public static class Judgment
{
    public const string NoAnswerFound = "no answer document was found";

    /// <summary>How long stopping a session after a failure may take before it is given up.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(30);

    public static Task<JudgmentOutcome> RunAsync(
        IAgentExecutor executor,
        JudgmentRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(executor, request, onSpawned: null, spend: null, stopping: CancellationToken.None, cancellationToken);

    public static Task<JudgmentOutcome> RunAsync(
        IAgentExecutor executor,
        JudgmentRequest request,
        Func<SessionHandle, CancellationToken, ValueTask>? onSpawned,
        CancellationToken stopping,
        CancellationToken cancellationToken) =>
        RunAsync(executor, request, onSpawned, spend: null, stopping, cancellationToken);

    /// <summary>
    /// Runs the judgment, handing the session's handle to <paramref name="onSpawned"/> as soon as the
    /// spawn returns, so the runner can record it before the first collect. Once the handle is
    /// handed over, a failure while <paramref name="stopping"/> is cancelled (the daemon stopping)
    /// leaves the session alive for a reattach; any other failure, a cancellation of
    /// <paramref name="cancellationToken"/> alone (a wall-clock limit, a person's pause or stop) or
    /// one the executor throws of its own accord among them, stops it. <paramref name="spend"/> is told every attempt's usage as it is
    /// collected and what that stop reports for the attempt in progress, so a judgment that ends
    /// without an outcome still says what it spent.
    /// </summary>
    public static async Task<JudgmentOutcome> RunAsync(
        IAgentExecutor executor,
        JudgmentRequest request,
        Func<SessionHandle, CancellationToken, ValueTask>? onSpawned,
        JudgmentSpend? spend,
        CancellationToken stopping,
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
            request.Access,
            cancellationToken);

        bool handedOver = false;
        try
        {
            if (onSpawned is not null)
            {
                await onSpawned(handle, cancellationToken);
                handedOver = true;
            }

            return await AnswerAsync(executor, handle, request.AnswerSchema, spend, cancellationToken, cancellationToken);
        }
        catch (Exception failure) when (!(handedOver && stopping.IsCancellationRequested))
        {
            await StopAfterFailureAsync(executor, handle, failure, spend);
            throw;
        }
    }

    /// <summary>
    /// Runs the judgment on a session launched before a daemon restart that the executor still
    /// holds: collects it, validates its answer against <paramref name="answerSchema"/>, and retries
    /// once on the same session after a missing or invalid answer, exactly as for a fresh spawn.
    /// Nothing is spawned. A session already on a later attempt than its spawn's was resumed for the
    /// retry before the restart, so its answer is the retry's: it is not retried again, and the
    /// usage its earlier attempts' collects returned, which nothing recorded at the daemon stop, is
    /// counted with its own. A failure while <paramref name="stopping"/> is cancelled leaves the
    /// session held for the next start to reattach again; any other failure stops it, and a token
    /// cancelled before the first collect (a wall-clock limit already past) is such a failure for a
    /// session still running. A session that had already <see cref="ReattachedSession.Finished"/>
    /// ran no longer than it did, so its collect, which returns at once, is bounded by
    /// <paramref name="finishedCollect"/> (the daemon stop and a person's pause or stop, not the
    /// wall-clock limit): its answer is recorded however late the restart came, and only a retry it
    /// needs is refused once the limit is past.
    /// </summary>
    public static async Task<JudgmentOutcome> ReattachAsync(
        IAgentExecutor executor,
        ReattachedSession session,
        string answerSchema,
        JudgmentSpend? spend,
        CancellationToken stopping,
        CancellationToken finishedCollect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(session);
        bool retrying = session.Attempt > 1;
        if (retrying)
        {
            spend?.Add(session.Earlier);
        }

        try
        {
            AnswerSchemas.Require(answerSchema);
            CancellationToken collecting = session.Finished ? finishedCollect : cancellationToken;
            collecting.ThrowIfCancellationRequested();
            return retrying
                ? await RetriedAsync(executor, session.Handle, answerSchema, session.Earlier, spend, collecting)
                : await AnswerAsync(executor, session.Handle, answerSchema, spend, collecting, cancellationToken);
        }
        catch (Exception failure) when (!stopping.IsCancellationRequested)
        {
            await StopAfterFailureAsync(executor, session.Handle, failure, spend);
            throw;
        }
    }

    /// <summary>
    /// Collects the session's first attempt under <paramref name="firstCollect"/>, and answers with
    /// it or resumes the session for its one retry under <paramref name="cancellationToken"/>.
    /// </summary>
    private static async Task<JudgmentOutcome> AnswerAsync(
        IAgentExecutor executor,
        SessionHandle handle,
        string answerSchema,
        JudgmentSpend? spend,
        CancellationToken firstCollect,
        CancellationToken cancellationToken)
    {
        CollectedAnswer first = await executor.CollectAsync(handle, firstCollect);
        spend?.Add(first.Usage);
        IReadOnlyList<string> firstFailures = Check(answerSchema, first.Answer);
        if (first.Answer is { } firstAnswer && firstFailures.Count == 0)
        {
            return new JudgmentOutcome.Answered(handle, firstAnswer, first.TranscriptPath, first.Usage);
        }

        await executor.ResumeAsync(handle, RetryMessage(answerSchema, first.Answer, firstFailures), cancellationToken);
        return await RetriedAsync(executor, handle, answerSchema, first.Usage, spend, cancellationToken);
    }

    /// <summary>
    /// Collects the session's one retry, whose earlier attempts' collects returned
    /// <paramref name="earlier"/>, and answers with it or pauses: there is no retry after it.
    /// </summary>
    private static async Task<JudgmentOutcome> RetriedAsync(
        IAgentExecutor executor,
        SessionHandle handle,
        string answerSchema,
        Measurement earlier,
        JudgmentSpend? spend,
        CancellationToken cancellationToken)
    {
        CollectedAnswer second = await executor.CollectAsync(handle, cancellationToken);
        spend?.Add(second.Usage);
        Measurement usage = Sum(earlier, second.Usage);
        IReadOnlyList<string> secondFailures = Check(answerSchema, second.Answer);
        return second.Answer is { } secondAnswer && secondFailures.Count == 0
            ? new JudgmentOutcome.Answered(handle, secondAnswer, second.TranscriptPath, usage)
            : new JudgmentOutcome.Paused(
                handle,
                $"Agent '{handle.Agent}' gave no valid '{answerSchema}' answer after one retry: {string.Join("; ", secondFailures)}.",
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
    /// Stops a session the judgment can no longer hand back. The failure may itself be the
    /// cancellation of the caller's token, so the stop takes a short, bounded token of its own
    /// rather than that one: an executor that honours its token still stops the session, and a
    /// stop that hangs gives up after <see cref="StopGrace"/>. This is the one place the judgment
    /// does not pass the caller's token on. What the stop reports for the attempt in progress is
    /// added to <paramref name="spend"/>. A stop that also fails is reported alongside the failure.
    /// </summary>
    private static async Task StopAfterFailureAsync(IAgentExecutor executor, SessionHandle handle, Exception failure, JudgmentSpend? spend)
    {
        try
        {
            using var bounded = new CancellationTokenSource(StopGrace);
            StopReport? inProgress = await executor.StopAsync(handle, bounded.Token);
            spend?.Add(inProgress);
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

    internal static Measurement Sum(Measurement first, Measurement second) => new(
        first.Tokens + second.Tokens,
        first.CostUsd + second.CostUsd,
        first.Turns + second.Turns,
        first.Duration + second.Duration);
}
