using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flows;
using Coanda.Core.Software;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.Executors;

public sealed class JudgmentTests
{
    private const string Agent = "reviewer-adversarial";
    private const string Valid = """{"verdict":"clean","findings":[]}""";
    private const string Invalid = """{"verdict":"maybe","findings":[]}""";
    private const string InvalidAgain = """{"findings":[]}""";

    private static readonly Measurement FirstUsage = new(1000, 0.25m, 4, TimeSpan.FromSeconds(30));
    private static readonly Measurement SecondUsage = new(300, 0.05m, 1, TimeSpan.FromSeconds(12));
    private static readonly Measurement Summed = new(1300, 0.30m, 5, TimeSpan.FromSeconds(42));

    private static readonly JudgmentRequest Request = new(
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

        JudgmentOutcome outcome = await Judgment.RunAsync(executor, Request, CancellationToken.None);

        SessionHandle session = executor.Spawns.Should().ContainSingle().Subject.Handle;
        outcome.Should().Be(new JudgmentOutcome.Answered(session, Valid, "/t/1.jsonl", FirstUsage));
        executor.Resumes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_first_answer_is_retried_by_one_resume_that_quotes_the_failure()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(Valid, "/t/2.jsonl", SecondUsage));
        string failure = AnswerValidator.Validate(AnswerSchemas.Review, Invalid).Failures[0];

        JudgmentOutcome outcome = await Judgment.RunAsync(executor, Request, CancellationToken.None);

        executor.Spawns.Should().ContainSingle();
        outcome.Should().Be(new JudgmentOutcome.Answered(executor.Spawns[0].Handle, Valid, "/t/2.jsonl", Summed));
        ScriptedMessage resume = executor.Resumes.Should().ContainSingle().Subject;
        resume.Handle.Should().Be(executor.Spawns[0].Handle);
        resume.Message.Should().Contain("'review'").And.Contain(failure);
    }

    [Fact]
    public async Task A_missing_first_answer_and_an_invalid_second_pause_with_the_second_transcript()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(null, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(InvalidAgain, "/t/2.jsonl", SecondUsage));
        string failure = AnswerValidator.Validate(AnswerSchemas.Review, InvalidAgain).Failures[0];

        JudgmentOutcome outcome = await Judgment.RunAsync(executor, Request, CancellationToken.None);

        executor.Spawns.Should().ContainSingle();
        executor.Resumes.Should().ContainSingle()
            .Which.Message.Should().Contain("No answer document was found").And.Contain("'review'");
        JudgmentOutcome.Paused pause = outcome.Should().BeOfType<JudgmentOutcome.Paused>().Subject;
        pause.Reason.Should().Contain($"'{Agent}'").And.Contain("'review'").And.Contain(failure);
        pause.Lever.Should().Be("/t/2.jsonl");
        pause.Usage.Should().Be(Summed);
    }

    [Fact]
    public async Task An_invalid_first_answer_and_an_invalid_second_pause_with_the_second_attempts_failures()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(InvalidAgain, "/t/2.jsonl", SecondUsage));
        string firstFailure = AnswerValidator.Validate(AnswerSchemas.Review, Invalid).Failures[0];
        string secondFailure = AnswerValidator.Validate(AnswerSchemas.Review, InvalidAgain).Failures[0];
        firstFailure.Should().NotBe(secondFailure, "the pause must be shown to quote the second attempt");

        JudgmentOutcome outcome = await Judgment.RunAsync(executor, Request, CancellationToken.None);

        executor.Spawns.Should().ContainSingle();
        executor.Resumes.Should().ContainSingle().Which.Message.Should().Contain("'review'").And.Contain(firstFailure);
        JudgmentOutcome.Paused pause = outcome.Should().BeOfType<JudgmentOutcome.Paused>().Subject;
        pause.Reason.Should().Contain($"'{Agent}'").And.Contain("'review'").And.Contain(secondFailure).And.NotContain(firstFailure);
        pause.Lever.Should().Be("/t/2.jsonl");
        pause.Usage.Should().Be(Summed);
    }

    [Fact]
    public async Task A_missing_second_answer_pauses_saying_none_was_found()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(null, "/t/2.jsonl", SecondUsage));

        JudgmentOutcome outcome = await Judgment.RunAsync(executor, Request, CancellationToken.None);

        outcome.Should().BeOfType<JudgmentOutcome.Paused>()
            .Which.Reason.Should().Contain(Judgment.NoAnswerFound);
    }

    [Fact]
    public async Task Either_outcome_hands_back_the_session_it_ran_in_and_leaves_it_open()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(InvalidAgain, "/t/2.jsonl", SecondUsage));

        JudgmentOutcome outcome = await Judgment.RunAsync(executor, Request, CancellationToken.None);

        outcome.Should().BeOfType<JudgmentOutcome.Paused>();
        outcome.Session.Should().Be(executor.Spawns.Should().ContainSingle().Subject.Handle);
        executor.Stops.Should().BeEmpty();
        (await executor.ListAsync(CancellationToken.None)).Should().Equal(outcome.Session);
    }

    [Fact]
    public async Task The_spawned_handle_reaches_the_caller_before_the_first_collect()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));
        SessionHandle? received = null;
        int spawnsSeen = -1;
        int resumesSeen = -1;

        JudgmentOutcome outcome = await Judgment.RunAsync(
            executor,
            Request,
            (handle, _) =>
            {
                received = handle;
                spawnsSeen = executor.Spawns.Count;
                resumesSeen = executor.Resumes.Count;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        received.Should().Be(outcome.Session);
        spawnsSeen.Should().Be(1);
        resumesSeen.Should().Be(0);
    }

    [Fact]
    public async Task A_failure_after_the_spawn_stops_the_session_and_rethrows()
    {
        ScriptedExecutor executor = Executor();

        Func<Task> run = () => Judgment.RunAsync(executor, Request, CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no entry 1*");
        executor.Stops.Should().Equal(executor.Spawns.Should().ContainSingle().Subject.Handle);
        (await executor.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancellation_after_the_caller_took_the_handle_leaves_the_session_for_a_reattach()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));
        using var cancellation = new CancellationTokenSource();
        SessionHandle? received = null;

        Func<Task> run = () => Judgment.RunAsync(
            executor,
            Request,
            async (handle, _) =>
            {
                received = handle;
                await cancellation.CancelAsync();
            },
            cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        executor.Stops.Should().BeEmpty();
        SessionHandle taken = received.Should().NotBeNull().And.BeOfType<SessionHandle>().Subject;
        (await executor.ReattachAsync(taken, CancellationToken.None)).Should().Be(taken);
    }

    [Fact]
    public async Task A_cancellation_before_the_caller_took_the_handle_stops_the_session()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));

        Func<Task> run = () => Judgment.RunAsync(
            executor,
            Request,
            (_, _) => throw new OperationCanceledException(),
            CancellationToken.None);

        await run.Should().ThrowAsync<OperationCanceledException>();
        executor.Stops.Should().Equal(executor.Spawns.Should().ContainSingle().Subject.Handle);
    }

    [Fact]
    public async Task An_unknown_schema_is_refused_before_anything_is_spawned()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));

        Func<Task> run = () => Judgment.RunAsync(executor, Request with { AnswerSchema = "reveiw" }, CancellationToken.None);

        await run.Should().ThrowAsync<DomainException>().WithMessage("*'reveiw'*");
        executor.Spawns.Should().BeEmpty();
    }

    [Fact]
    public async Task A_schema_other_than_the_agents_own_is_refused_before_anything_is_spawned()
    {
        ScriptedExecutor executor = Executor(new ScriptEntry(Valid, "/t/1.jsonl", FirstUsage));

        Func<Task> run = () => Judgment.RunAsync(executor, Request with { AnswerSchema = AnswerSchemas.Build }, CancellationToken.None);

        await run.Should().ThrowAsync<DomainException>().WithMessage($"*'{AnswerSchemas.Build}'*'{Agent}'*'{AnswerSchemas.Review}'*");
        executor.Spawns.Should().BeEmpty();
    }
}
