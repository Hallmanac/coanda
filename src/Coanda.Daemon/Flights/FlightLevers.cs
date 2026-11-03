namespace Coanda.Daemon.Flights;

/// <summary>The lever a park carries when nothing more specific names what a person acts on.</summary>
public static class FlightLevers
{
    /// <summary>Names the step a resume performs again.</summary>
    public static string PerformAgain(string step) => $"Resume the flight to perform step '{step}' again.";

    /// <summary>Names the way out of a flight that has spawned as many agents as its flow allows: a resume cannot spawn another.</summary>
    public static string SpawnCapReached(string flow) =>
        $"Stop the flight; flow '{flow}' allows no more spawns, so a resume cannot perform another judgment.";
}
