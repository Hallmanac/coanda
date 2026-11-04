using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.Software;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.Executors;

public sealed class AgentStepTests
{
    private const string Agent = "reviewer-adversarial";
    private const string Valid = """{"verdict":"clean","findings":[]}""";
    private const string Invalid = """{"verdict":"maybe","findings":[]}""";
    private const string InvalidAgain = """{"findings":[]}""";

    private static readonly Measurement FirstUsage = new(1000, 0.25m, 4, TimeSpan.FromSeconds(30));
    private static readonly Measurement SecondUsage = new(300, 0.05m, 1, TimeSpan.FromSeconds(12));
    private static readonly Measurement Summed = new(1300, 0.30m, 5, TimeSpan.FromSeconds(42));

    private static readonly AgentStepRequest Request = new(
        ShippedAgents.ReviewerAdversarial,
        "Step: review",
        "/work/tree",
        new SpawnCaps(3m, TimeSpan.FromMinutes(30)),
        AnswerSchemas.Review);

    private static ScriptedExecutor Executor(params ScriptEntry[] entries) =>
        new(new Dictionary<string, IReadOnlyList<ScriptEntry>> { [Agent] = entries });

    [Fact]
    public async Task A_valid_first_answer_is_returned_after_one_spawn_and_no_resume()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));

        AgentStepOutcome outcome = await AgentStep.RunAsync(executor, Request, CancellationToken.None);

        SessionHandle session = Assert.Single(executor.Spawns).Handle;
        Assert.Equal(new AgentStepOutcome.Answered(session, Valid, "/t/1.jsonl", FirstUsage), outcome);
        Assert.Empty(executor.Resumes);
    }

    [Fact]
    public async Task An_invalid_first_answer_is_retried_by_one_resume_that_quotes_the_failure()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(Valid, "/t/2.jsonl", SecondUsage));
        string failure = AnswerValidator.Validate(AnswerSchemas.Review, Invalid).Failures[0];

        AgentStepOutcome outcome = await AgentStep.RunAsync(executor, Request, CancellationToken.None);

        Assert.Single(executor.Spawns);
        Assert.Equal(new AgentStepOutcome.Answered(executor.Spawns[0].Handle, Valid, "/t/2.jsonl", Summed), outcome);
        ScriptedMessage resume = Assert.Single(executor.Resumes);
        Assert.Equal(executor.Spawns[0].Handle, resume.Handle);
        Assert.Contains("'review'", resume.Message, StringComparison.Ordinal);
        Assert.Contains(failure, resume.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_first_answer_and_an_invalid_second_pause_with_the_second_transcript()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(null, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(InvalidAgain, "/t/2.jsonl", SecondUsage));
        string failure = AnswerValidator.Validate(AnswerSchemas.Review, InvalidAgain).Failures[0];

        AgentStepOutcome outcome = await AgentStep.RunAsync(executor, Request, CancellationToken.None);

        Assert.Single(executor.Spawns);
        ScriptedMessage resume = Assert.Single(executor.Resumes);
        Assert.Contains("No answer document was found", resume.Message, StringComparison.Ordinal);
        Assert.Contains("'review'", resume.Message, StringComparison.Ordinal);
        AgentStepOutcome.Paused pause = Assert.IsType<AgentStepOutcome.Paused>(outcome);
        Assert.Contains($"'{Agent}'", pause.Reason, StringComparison.Ordinal);
        Assert.Contains("'review'", pause.Reason, StringComparison.Ordinal);
        Assert.Contains(failure, pause.Reason, StringComparison.Ordinal);
        Assert.Equal("/t/2.jsonl", pause.Lever);
        Assert.Equal(Summed, pause.Usage);
    }

    [Fact]
    public async Task An_invalid_first_answer_and_an_invalid_second_pause_with_the_second_attempts_failures()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(InvalidAgain, "/t/2.jsonl", SecondUsage));
        string firstFailure = AnswerValidator.Validate(AnswerSchemas.Review, Invalid).Failures[0];
        string secondFailure = AnswerValidator.Validate(AnswerSchemas.Review, InvalidAgain).Failures[0];
        // the pause must be shown to quote the second attempt
        Assert.NotEqual(secondFailure, firstFailure);

        AgentStepOutcome outcome = await AgentStep.RunAsync(executor, Request, CancellationToken.None);

        Assert.Single(executor.Spawns);
        ScriptedMessage resume = Assert.Single(executor.Resumes);
        Assert.Contains("'review'", resume.Message, StringComparison.Ordinal);
        Assert.Contains(firstFailure, resume.Message, StringComparison.Ordinal);
        AgentStepOutcome.Paused pause = Assert.IsType<AgentStepOutcome.Paused>(outcome);
        Assert.Contains($"'{Agent}'", pause.Reason, StringComparison.Ordinal);
        Assert.Contains("'review'", pause.Reason, StringComparison.Ordinal);
        Assert.Contains(secondFailure, pause.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(firstFailure, pause.Reason, StringComparison.Ordinal);
        Assert.Equal("/t/2.jsonl", pause.Lever);
        Assert.Equal(Summed, pause.Usage);
    }

    [Fact]
    public async Task A_missing_second_answer_pauses_saying_none_was_found()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(null, "/t/2.jsonl", SecondUsage));

        AgentStepOutcome outcome = await AgentStep.RunAsync(executor, Request, CancellationToken.None);

        Assert.Contains(AgentStep.NoAnswerFound, Assert.IsType<AgentStepOutcome.Paused>(outcome).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Either_outcome_hands_back_the_session_it_ran_in_and_leaves_it_open()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(InvalidAgain, "/t/2.jsonl", SecondUsage));

        AgentStepOutcome outcome = await AgentStep.RunAsync(executor, Request, CancellationToken.None);

        Assert.IsType<AgentStepOutcome.Paused>(outcome);
        Assert.Equal(Assert.Single(executor.Spawns).Handle, outcome.Session);
        Assert.Empty(executor.Stops);
        Assert.Equal([outcome.Session], await executor.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_spawned_handle_reaches_the_caller_before_the_first_collect()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));
        SessionHandle? received = null;
        int spawnsSeen = -1;
        int resumesSeen = -1;

        AgentStepOutcome outcome = await AgentStep.RunAsync(
            executor,
            Request,
            (handle, _) =>
            {
                received = handle;
                spawnsSeen = executor.Spawns.Count;
                resumesSeen = executor.Resumes.Count;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None,
            CancellationToken.None);

        Assert.Equal(outcome.Session, received);
        Assert.Equal(1, spawnsSeen);
        Assert.Equal(0, resumesSeen);
    }

    [Fact]
    public async Task A_failure_after_the_spawn_stops_the_session_and_rethrows()
    {
        ScriptedExecutor executor = Executor();

        Func<Task> run = () => AgentStep.RunAsync(executor, Request, CancellationToken.None);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(run);
        Assert.Contains("no entry 1", exception.Message, StringComparison.Ordinal);
        Assert.Equal([Assert.Single(executor.Spawns).Handle], executor.Stops);
        Assert.Empty(await executor.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_cancellation_after_the_caller_took_the_handle_leaves_the_session_for_a_reattach()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));
        using var cancellation = new CancellationTokenSource();
        SessionHandle? received = null;

        Func<Task> run = () => AgentStep.RunAsync(
            executor,
            Request,
            async (handle, _) =>
            {
                received = handle;
                await cancellation.CancelAsync();
            },
            cancellation.Token,
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        Assert.Empty(executor.Stops);
        Assert.NotNull(received);
        SessionHandle taken = Assert.IsType<SessionHandle>(received);
        Assert.Equal(ReattachedSession.OnSpawn(taken), await executor.ReattachAsync(taken, access: null, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancellation_after_the_caller_took_the_handle_while_the_caller_is_not_stopping_stops_the_session()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));
        using var limit = new CancellationTokenSource();

        Func<Task> run = () => AgentStep.RunAsync(
            executor,
            Request,
            async (_, _) => await limit.CancelAsync(),
            CancellationToken.None,
            limit.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        Assert.Equal([Assert.Single(executor.Spawns).Handle], executor.Stops);
        Assert.Empty(await executor.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_cancellation_before_the_caller_took_the_handle_stops_the_session()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));

        Func<Task> run = () => AgentStep.RunAsync(
            executor,
            Request,
            (_, _) => throw new OperationCanceledException(),
            CancellationToken.None,
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        Assert.Equal([Assert.Single(executor.Spawns).Handle], executor.Stops);
    }

    [Fact]
    public async Task An_unknown_schema_is_refused_before_anything_is_spawned()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));

        Func<Task> run = () => AgentStep.RunAsync(executor, Request with { AnswerSchema = "reveiw" }, CancellationToken.None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(run);
        Assert.Contains("'reveiw'", exception.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Spawns);
    }

    [Fact]
    public async Task A_schema_other_than_the_agents_own_is_refused_before_anything_is_spawned()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));

        Func<Task> run = () => AgentStep.RunAsync(executor, Request with { AnswerSchema = AnswerSchemas.Build }, CancellationToken.None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(run);
        Assert.Matches(WildcardPattern.ToRegex($"*'{AnswerSchemas.Build}'*'{Agent}'*'{AnswerSchemas.Review}'*"), exception.Message);
        Assert.Empty(executor.Spawns);
    }

    [Fact]
    public async Task A_reattached_session_is_collected_validated_and_retried_once_on_the_same_session_without_a_spawn()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(Valid, "/t/2.jsonl", SecondUsage));
        SessionHandle held = await SpawnAsync(executor);

        AgentStepOutcome outcome = await AgentStep.ReattachAsync(executor, ReattachedSession.OnSpawn(held), AnswerSchemas.Review, spend: null, CancellationToken.None, CancellationToken.None, CancellationToken.None);

        Assert.Equal(new AgentStepOutcome.Answered(held, Valid, "/t/2.jsonl", Summed), outcome);
        // the reattach spawns nothing
        Assert.Single(executor.Spawns);
        Assert.Equal(held, Assert.Single(executor.Resumes).Handle);
        // the caller stops the session once the outcome is recorded
        Assert.Empty(executor.Stops);
    }

    [Fact]
    public async Task A_session_reattached_during_its_retry_answers_with_the_whole_sessions_usage_and_is_not_retried_again()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(Invalid, "/t/2.jsonl", SecondUsage));
        SessionHandle held = await SpawnAsync(executor);
        await executor.CollectAsync(held, CancellationToken.None);
        await executor.ResumeAsync(held, "Answer again.", CancellationToken.None);
        Reattachment? actual = await executor.ReattachAsync(held, access: null, CancellationToken.None);
        Assert.NotNull(actual);
        ReattachedSession retrying = Assert.IsType<ReattachedSession>(actual);
        Assert.Equal(new ReattachedSession(held, 2, FirstUsage), retrying);
        var spend = new AgentStepSpend();

        AgentStepOutcome outcome = await AgentStep.ReattachAsync(executor, retrying, AnswerSchemas.Review, spend, CancellationToken.None, CancellationToken.None, CancellationToken.None);

        AgentStepOutcome.Paused paused = Assert.IsType<AgentStepOutcome.Paused>(outcome);
        Assert.StartsWith($"Agent '{Agent}' gave no valid '{AnswerSchemas.Review}' answer after one retry:", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(Summed, paused.Usage);
        Assert.Equal(Summed, spend.Reported);
        // the retry before the restart was the one retry
        Assert.Single(executor.Resumes);
    }

    [Fact]
    public async Task A_session_reattached_during_its_retry_and_stopped_before_it_answers_reports_its_earlier_usage()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage));
        SessionHandle held = await SpawnAsync(executor);
        await executor.CollectAsync(held, CancellationToken.None);
        await executor.ResumeAsync(held, "Answer again.", CancellationToken.None);
        using var limit = new CancellationTokenSource();
        await limit.CancelAsync();
        var spend = new AgentStepSpend();

        Func<Task> run = () => AgentStep.ReattachAsync(executor, new ReattachedSession(held, 2, FirstUsage), AnswerSchemas.Review, spend, CancellationToken.None, CancellationToken.None, limit.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        Assert.Equal([held], executor.Stops);
        // nothing recorded the earlier attempt's usage at the daemon stop
        Assert.Equal(FirstUsage, spend.Reported);
    }

    [Fact]
    public async Task A_reattached_session_whose_token_is_already_cancelled_is_stopped_without_a_collect()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));
        SessionHandle held = await SpawnAsync(executor);
        using var limit = new CancellationTokenSource();
        await limit.CancelAsync();

        Func<Task> run = () => AgentStep.ReattachAsync(executor, ReattachedSession.OnSpawn(held), AnswerSchemas.Review, spend: null, CancellationToken.None, CancellationToken.None, limit.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        Assert.Equal([held], executor.Stops);
    }

    [Fact]
    public async Task A_reattached_session_that_finished_is_collected_and_answers_though_its_limit_has_passed()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));
        SessionHandle held = await SpawnAsync(executor);
        executor.Finish(held);
        ReattachedSession finished = Assert.IsType<ReattachedSession>(await executor.ReattachAsync(held, access: null, CancellationToken.None));
        Assert.True(finished.Finished);
        using var limit = new CancellationTokenSource();
        await limit.CancelAsync();

        AgentStepOutcome outcome = await AgentStep.ReattachAsync(executor, finished, AnswerSchemas.Review, spend: null, CancellationToken.None, CancellationToken.None, limit.Token);

        Assert.Equal(new AgentStepOutcome.Answered(held, Valid, "/t/1.jsonl", FirstUsage), outcome);
        // the caller stops the session once the outcome is recorded
        Assert.Empty(executor.Stops);
    }

    [Fact]
    public async Task A_reattached_session_that_finished_with_an_invalid_answer_after_its_limit_is_stopped_without_a_retry()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage));
        SessionHandle held = await SpawnAsync(executor);
        executor.Finish(held);
        using var limit = new CancellationTokenSource();
        await limit.CancelAsync();
        var spend = new AgentStepSpend();

        Func<Task> run = () => AgentStep.ReattachAsync(
            executor, new ReattachedSession(held, 1, ReattachedSession.Nothing, Finished: true), AnswerSchemas.Review, spend, CancellationToken.None, CancellationToken.None, limit.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        // a retry would run past the limit
        Assert.Empty(executor.Resumes);
        Assert.Equal([held], executor.Stops);
        // the finished attempt's collect returned its usage
        Assert.Equal(FirstUsage, spend.Reported);
    }

    [Fact]
    public async Task A_reattached_session_that_finished_is_not_collected_once_a_person_paused_it()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));
        SessionHandle held = await SpawnAsync(executor);
        using var ended = new CancellationTokenSource();
        await ended.CancelAsync();

        Func<Task> run = () => AgentStep.ReattachAsync(
            executor, new ReattachedSession(held, 1, ReattachedSession.Nothing, Finished: true), AnswerSchemas.Review, spend: null, CancellationToken.None, ended.Token, ended.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        Assert.Equal([held], executor.Stops);
    }

    [Fact]
    public async Task A_reattached_session_failing_while_the_caller_stops_is_left_for_the_next_reattach()
    {
        ScriptedExecutor executor = Executor();
        SessionHandle held = await SpawnAsync(executor);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        Func<Task> run = () => AgentStep.ReattachAsync(executor, ReattachedSession.OnSpawn(held), AnswerSchemas.Review, spend: null, stopping.Token, CancellationToken.None, CancellationToken.None);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(run);
        Assert.Contains("no entry 1", exception.Message, StringComparison.Ordinal);
        // a failure while the daemon stops is a daemon stop, whatever its type
        Assert.Empty(executor.Stops);
        Assert.Equal(ReattachedSession.OnSpawn(held), await executor.ReattachAsync(held, access: null, CancellationToken.None));
    }

    [Fact]
    public async Task A_reattached_session_failing_while_the_caller_is_not_stopping_is_stopped_and_the_failure_rethrown()
    {
        ScriptedExecutor executor = Executor();
        SessionHandle held = await SpawnAsync(executor);

        Func<Task> run = () => AgentStep.ReattachAsync(executor, ReattachedSession.OnSpawn(held), AnswerSchemas.Review, spend: null, CancellationToken.None, CancellationToken.None, CancellationToken.None);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(run);
        Assert.Contains("no entry 1", exception.Message, StringComparison.Ordinal);
        Assert.Equal([held], executor.Stops);
    }

    private static Task<SessionHandle> SpawnAsync(ScriptedExecutor executor) =>
        executor.SpawnAsync(Request.Agent, Request.Context, Request.WorkingDirectory, Request.Caps, Request.AnswerSchema, null, null, CancellationToken.None).AsTask();
}
