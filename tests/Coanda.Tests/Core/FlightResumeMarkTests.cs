using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flights;
using Coanda.Core.Flights.Events;

namespace Coanda.Tests.Core;

public sealed class FlightResumeMarkTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_resume_marks_the_current_visit_and_a_recording_clears_the_mark_so_a_later_resume_alone_marks_a_recorded_visit()
    {
        Guid flightId = Ids.New();
        Flight flight = Flight.Create(new FlightStarted(
            flightId, Ids.New(), "hold", 1, "hash", "owner", "approve", StepKind.Action, "wait-for-human", At));

        flight.Apply(new FlightParked(flightId, "Waits for a person.", "Resume it.", "flight", At.AddMinutes(1)));
        flight.Apply(new FlightResumed(flightId, "owner", string.Empty, At.AddMinutes(2)));
        flight.Current?.ResumedAt.Should().Be(At.AddMinutes(2), "the resumed visit is performed again");

        flight.Apply(new ActionCompleted(flightId, "approve", "wait-for-human", JsonSerializer.SerializeToElement(new { verdict = "done" }), At.AddMinutes(3)));
        flight.Current?.ResumedAt.Should().BeNull("a recording consumes the resume, so a restart routes it rather than performing it again");

        flight.Apply(new FlightParked(flightId, "Capped.", "Resume it.", "flight", At.AddMinutes(4)));
        flight.Apply(new FlightResumed(flightId, "owner", string.Empty, At.AddMinutes(5)));
        flight.Current?.IsRecorded.Should().BeTrue();
        flight.Current?.ResumedAt.Should().Be(At.AddMinutes(5), "a resume after the recording asks for the step to be performed again");
    }
}
