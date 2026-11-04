namespace Coanda.Daemon.Flights;

/// <summary>The lever a pause carries when nothing more specific names what a person acts on.</summary>
public static class FlightLevers
{
    /// <summary>Names the step a resume performs again.</summary>
    public static string PerformAgain(string step) => $"Resume the flight to perform step '{step}' again.";

    /// <summary>Names the way out of a flight that has spawned as many agents as its flow allows: a resume cannot spawn another.</summary>
    public static string SpawnCapReached(string flow) =>
        $"Stop the flight; flow '{flow}' allows no more spawns, so a resume cannot perform another judgment.";

    /// <summary>Names the way out of a flight deeper in its chain than its flow's follow allows: a resume pauses it again.</summary>
    public static string DepthCapReached(string flow) =>
        $"Stop the flight; it is past the depth cap of flow '{flow}''s follow, so a resume pauses it again.";

    /// <summary>Names the way out of a flight whose budget, or its chain's, is spent: a resume pauses it again.</summary>
    public static string BudgetReached() =>
        "Stop the flight; its budget is spent, so a resume pauses it again before any judgment.";
}
