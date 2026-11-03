using Coanda.Core.Agents;
using Coanda.Core.Flights;
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

        outcome.Should().Be(new JudgmentOutcome.Answered(Valid, "/t/1.jsonl", FirstUsage));
        executor.Spawns.Should().ContainSingle();
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

        outcome.Should().Be(new JudgmentOutcome.Answered(Valid, "/t/2.jsonl", Summed));
        executor.Spawns.Should().ContainSingle();
        ScriptedMessage resume = executor.Resumes.Should().ContainSingle().Subject;
        resume.Handle.Should().Be(executor.Spawns[0].Handle);
        resume.Message.Should().Contain("'review'").And.Contain(failure);
    }

    [Fact]
    public async Task A_missing_first_answer_and_an_invalid_second_park_with_the_second_transcript()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(null, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(InvalidAgain, "/t/2.jsonl", SecondUsage));
        string failure = AnswerValidator.Validate(AnswerSchemas.Review, InvalidAgain).Failures[0];

        JudgmentOutcome outcome = await Judgment.RunAsync(executor, Request, CancellationToken.None);

        executor.Spawns.Should().ContainSingle();
        executor.Resumes.Should().ContainSingle()
            .Which.Message.Should().Contain("No answer document was found").And.Contain("'review'");
        JudgmentOutcome.Parked park = outcome.Should().BeOfType<JudgmentOutcome.Parked>().Subject;
        park.Reason.Should().Contain($"'{Agent}'").And.Contain("'review'").And.Contain(failure);
        park.Lever.Should().Be("/t/2.jsonl");
        park.Usage.Should().Be(Summed);
    }

    [Fact]
    public async Task An_invalid_first_answer_and_an_invalid_second_park_with_the_second_attempts_failures()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(InvalidAgain, "/t/2.jsonl", SecondUsage));
        string firstFailure = AnswerValidator.Validate(AnswerSchemas.Review, Invalid).Failures[0];
        string secondFailure = AnswerValidator.Validate(AnswerSchemas.Review, InvalidAgain).Failures[0];
        firstFailure.Should().NotBe(secondFailure, "the park must be shown to quote the second attempt");

        JudgmentOutcome outcome = await Judgment.RunAsync(executor, Request, CancellationToken.None);

        executor.Spawns.Should().ContainSingle();
        executor.Resumes.Should().ContainSingle().Which.Message.Should().Contain("'review'").And.Contain(firstFailure);
        JudgmentOutcome.Parked park = outcome.Should().BeOfType<JudgmentOutcome.Parked>().Subject;
        park.Reason.Should().Contain($"'{Agent}'").And.Contain("'review'").And.Contain(secondFailure).And.NotContain(firstFailure);
        park.Lever.Should().Be("/t/2.jsonl");
        park.Usage.Should().Be(Summed);
    }

    [Fact]
    public async Task A_missing_second_answer_parks_saying_none_was_found()
    {
        ScriptedExecutor executor = Executor(
            new ScriptEntry(Invalid, "/t/1.jsonl", FirstUsage),
            new ScriptEntry(null, "/t/2.jsonl", SecondUsage));

        JudgmentOutcome outcome = await Judgment.RunAsync(executor, Request, CancellationToken.None);

        outcome.Should().BeOfType<JudgmentOutcome.Parked>()
            .Which.Reason.Should().Contain(Judgment.NoAnswerFound);
    }
}
