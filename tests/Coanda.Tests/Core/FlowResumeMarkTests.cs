using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Flows.Events;

namespace Coanda.Tests.Core;

public sealed class FlowResumeMarkTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_resume_marks_the_current_visit_and_a_recording_clears_the_mark_so_a_later_resume_alone_marks_a_recorded_visit()
    {
        Guid flowId = Ids.New();
        Flow flow = Flow.Create(new FlowStarted(
            flowId, Ids.New(), "hold", 1, "hash", "owner", "approve", StepKind.Action, "wait-for-human", At));

        flow.Apply(new FlowPaused(flowId, "Waits for a person.", "Resume it.", "flow", At.AddMinutes(1)));
        flow.Apply(new FlowResumed(flowId, "owner", string.Empty, At.AddMinutes(2)));
        flow.Current?.ResumedAt.Should().Be(At.AddMinutes(2), "the resumed visit is performed again");

        flow.Apply(new ActionCompleted(flowId, "approve", "wait-for-human", JsonSerializer.SerializeToElement(new { verdict = "done" }), At.AddMinutes(3)));
        flow.Current?.ResumedAt.Should().BeNull("a recording consumes the resume, so a restart routes it rather than performing it again");

        flow.Apply(new FlowPaused(flowId, "Capped.", "Resume it.", "flow", At.AddMinutes(4)));
        flow.Apply(new FlowResumed(flowId, "owner", string.Empty, At.AddMinutes(5)));
        flow.Current?.IsRecorded.Should().BeTrue();
        flow.Current?.ResumedAt.Should().Be(At.AddMinutes(5), "a resume after the recording asks for the step to be performed again");
    }
}
