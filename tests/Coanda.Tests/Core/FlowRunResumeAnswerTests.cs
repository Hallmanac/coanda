using System.Text.Json;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;

namespace Coanda.Tests.Core;

/// <summary>
/// A person's answer on a resume: its rule, the trimmed answer the resumed event records, and the copy
/// the visit the resume landed on keeps through its recording.
/// </summary>
public sealed class FlowRunResumeAnswerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Flow Build = SoftwareFlows.Build;

    /// <summary>A build flow run paused at its first step, the worktree system step, which has recorded nothing.</summary>
    private static FlowRun PausedUnrecorded()
    {
        Parcel parcel = Parcel.Create(new ParcelCreated(Ids.New(), "alpha", "task", "hash", "build", "A parcel", "owner", Now));
        (FlowRunStarted started, _) = FlowRunDecider.Start(Build, 1, parcel, "owner", Now);
        FlowRun flowRun = FlowRun.Create(started);
        Pause(flowRun);
        return flowRun;
    }

    private static void Pause(FlowRun flowRun) =>
        flowRun.Apply(FlowRunDecider.Pause(Build, flowRun, "waiting", "answer the question", "owner", Now));

    private static void Record(FlowRun flowRun, DateTimeOffset at) =>
        flowRun.Apply(new SystemStepCompleted(
            flowRun.Id, flowRun.CurrentStep, "worktree", JsonSerializer.SerializeToElement(new { verdict = "done" }), at));

    [Fact]
    public void A_resume_with_an_answer_records_it_trimmed_and_one_without_records_none()
    {
        FlowRunResumed answered = FlowRunDecider.Resume(Build, PausedUnrecorded(), "a note", "  approve  ", "owner", Now);
        FlowRunResumed unanswered = FlowRunDecider.Resume(Build, PausedUnrecorded(), "a note", null, "owner", Now);

        Assert.Equal("approve", answered.Answer);
        // the note is its own value and stays as it was
        Assert.Equal("a note", answered.Note);
        Assert.Null(unanswered.Answer);
    }

    [Fact]
    public void An_answer_of_exactly_the_limit_after_trimming_is_recorded()
    {
        string answer = new('a', FlowRunDecider.ResumeAnswerMaxLength);

        FlowRunResumed resumed = FlowRunDecider.Resume(Build, PausedUnrecorded(), null, $" {answer} ", "owner", Now);

        Assert.Equal(answer, resumed.Answer);
    }

    [Theory]
    [InlineData("", "A resume's answer is blank; give an answer that is not blank, or resume without one.")]
    [InlineData("   ", "A resume's answer is blank; give an answer that is not blank, or resume without one.")]
    [InlineData("\r\n", "A resume's answer is blank; give an answer that is not blank, or resume without one.")]
    [InlineData("yes\rno", "A resume's answer is a single line; it holds no carriage return, line feed, or other control character.")]
    [InlineData("yes\nno", "A resume's answer is a single line; it holds no carriage return, line feed, or other control character.")]
    [InlineData("yes\tno", "A resume's answer is a single line; it holds no carriage return, line feed, or other control character.")]
    [InlineData("yes\u0000", "A resume's answer is a single line; it holds no carriage return, line feed, or other control character.")]
    [InlineData("approve\n", "A resume's answer is a single line; it holds no carriage return, line feed, or other control character.")]
    public void A_blank_or_multi_line_answer_is_refused_with_its_rule(string answer, string rule)
    {
        FlowRun flowRun = PausedUnrecorded();

        Action resume = () => FlowRunDecider.Resume(Build, flowRun, null, answer, "owner", Now);

        DomainException exception = Assert.Throws<DomainException>(resume);
        Assert.Equal(rule, exception.Message);
        Assert.Equal(FlowRunStatus.Paused, flowRun.Status);
    }

    [Fact]
    public void An_answer_longer_than_the_limit_after_trimming_is_refused_with_its_rule()
    {
        string answer = $"  {new string('a', FlowRunDecider.ResumeAnswerMaxLength + 1)}  ";

        Action resume = () => FlowRunDecider.Resume(Build, PausedUnrecorded(), null, answer, "owner", Now);

        DomainException exception = Assert.Throws<DomainException>(resume);
        Assert.Equal("A resume's answer is at most 200 characters after trimming; this one is 201.", exception.Message);
    }

    [Fact]
    public void An_answer_is_counted_as_string_length_counts_it()
    {
        // 100 characters outside the basic multilingual plane are 200 UTF-16 code units.
        string answer = string.Concat(Enumerable.Repeat("\U0001F600", 100));

        FlowRunResumed resumed = FlowRunDecider.Resume(Build, PausedUnrecorded(), null, answer, "owner", Now);
        Action over = () => FlowRunDecider.Resume(Build, PausedUnrecorded(), null, answer + "a", "owner", Now);

        Assert.Equal(answer, resumed.Answer);
        Assert.EndsWith("this one is 201.", Assert.Throws<DomainException>(over).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_visit_the_resume_landed_on_keeps_the_answer_through_its_recording_and_no_other_visit_holds_it()
    {
        FlowRun flowRun = PausedUnrecorded();

        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, "approve", "owner", Now.AddMinutes(1)));
        Assert.Equal("approve", flowRun.Current?.ResumeAnswer);

        Record(flowRun, Now.AddMinutes(2));
        FlowRunStepEntry recorded = Assert.Single(flowRun.Steps);
        // a recording clears the resume's mark but not its answer
        Assert.Null(recorded.ResumedAt);
        Assert.Equal("approve", recorded.ResumeAnswer);
        Assert.Null(recorded.Answer);

        flowRun.Apply(FlowRunDecider.StartStep(Build, flowRun, "build", Now.AddMinutes(3)));
        Assert.Equal(["approve", null], flowRun.Steps.Select(s => s.ResumeAnswer));
        Assert.All(flowRun.Steps, s => Assert.Null(s.Answer));
    }

    [Fact]
    public void A_later_resume_of_an_unrecorded_visit_replaces_its_answer_or_leaves_it_with_none()
    {
        FlowRun flowRun = PausedUnrecorded();
        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, "approve", "owner", Now.AddMinutes(1)));

        Pause(flowRun);
        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, "reject", "owner", Now.AddMinutes(2)));
        Assert.Equal("reject", Assert.Single(flowRun.Steps).ResumeAnswer);

        Pause(flowRun);
        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, null, "owner", Now.AddMinutes(3)));
        Assert.Null(Assert.Single(flowRun.Steps).ResumeAnswer);
    }

    [Fact]
    public void A_resume_with_an_answer_of_a_recorded_visit_is_refused_and_one_without_leaves_its_answer()
    {
        FlowRun flowRun = PausedUnrecorded();
        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, "approve", "owner", Now.AddMinutes(1)));
        Record(flowRun, Now.AddMinutes(2));
        Pause(flowRun);

        Action answered = () => FlowRunDecider.Resume(Build, flowRun, null, "reject", "owner", Now.AddMinutes(3));

        DomainException exception = Assert.Throws<DomainException>(answered);
        Assert.Equal(
            $"Flow run {Ids.Short(flowRun.Id)}'s visit of step 'worktree' has recorded, so the resume performs it again as a new visit and an answer would reach no step; resume without one.",
            exception.Message);

        FlowRunResumed unanswered = FlowRunDecider.Resume(Build, flowRun, null, null, "owner", Now.AddMinutes(3));
        flowRun.Apply(unanswered);
        Assert.Null(unanswered.Answer);
        Assert.Equal(FlowRunStatus.Running, flowRun.Status);
        FlowRunStepEntry visit = Assert.Single(flowRun.Steps);
        Assert.Equal(Now.AddMinutes(3), visit.ResumedAt);
        Assert.Equal("approve", visit.ResumeAnswer);
    }

    [Fact]
    public void A_resume_of_a_flow_run_that_is_not_paused_is_refused_before_its_answer_is_judged()
    {
        FlowRun flowRun = PausedUnrecorded();
        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, null, "owner", Now));

        Action resume = () => FlowRunDecider.Resume(Build, flowRun, null, " ", "owner", Now);

        Assert.EndsWith("; only a paused flow run resumes.", Assert.Throws<DomainException>(resume).Message, StringComparison.Ordinal);
    }
}
