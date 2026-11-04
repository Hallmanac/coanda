using System.Text.Json;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;

namespace Coanda.Tests.Core;

public sealed class FlowRunResumeMarkTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_resume_marks_the_current_visit_and_a_recording_clears_the_mark_so_a_later_resume_alone_marks_a_recorded_visit()
    {
        Guid flowRunId = Ids.New();
        FlowRun flowRun = FlowRun.Create(new FlowRunStarted(
            flowRunId, Ids.New(), "hold", 1, "hash", "owner", "approve", StepKind.SystemStep, "wait-for-human", At));

        flowRun.Apply(new FlowRunPaused(flowRunId, "Waits for a person.", "Resume it.", "flow-run", At.AddMinutes(1)));
        flowRun.Apply(new FlowRunResumed(flowRunId, "owner", string.Empty, At.AddMinutes(2)));
        // the resumed visit is performed again
        Assert.Equal(At.AddMinutes(2), flowRun.Current?.ResumedAt);

        flowRun.Apply(new SystemStepCompleted(flowRunId, "approve", "wait-for-human", JsonSerializer.SerializeToElement(new { verdict = "done" }), At.AddMinutes(3)));
        // a recording consumes the resume, so a restart routes it rather than performing it again
        Assert.Null(flowRun.Current?.ResumedAt);

        flowRun.Apply(new FlowRunPaused(flowRunId, "Capped.", "Resume it.", "flow-run", At.AddMinutes(4)));
        flowRun.Apply(new FlowRunResumed(flowRunId, "owner", string.Empty, At.AddMinutes(5)));
        Assert.True(flowRun.Current?.IsRecorded);
        // a resume after the recording asks for the step to be performed again
        Assert.Equal(At.AddMinutes(5), flowRun.Current?.ResumedAt);
    }
}
