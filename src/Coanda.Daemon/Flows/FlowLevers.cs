namespace Coanda.Daemon.Flows;

/// <summary>The lever a pause carries when nothing more specific names what a person acts on.</summary>
public static class FlowLevers
{
    /// <summary>Names the step a resume performs again.</summary>
    public static string PerformAgain(string step) => $"Resume the flow to perform step '{step}' again.";

    /// <summary>Names the way out of a flow that has spawned as many agents as its contour allows: a resume cannot spawn another.</summary>
    public static string SpawnCapReached(string contour) =>
        $"Stop the flow; contour '{contour}' allows no more spawns, so a resume cannot perform another judgment.";

    /// <summary>Names the way out of a flow whose current step no transition's visit cap allows another visit to: a resume pauses it again.</summary>
    public static string VisitCapsSpent(string step) =>
        $"Stop the flow; every transition out of step '{step}' has spent its visit cap, so a resume pauses it again.";

    /// <summary>Names the way out of a flow deeper in its chain than its contour's follow allows: a resume pauses it again.</summary>
    public static string DepthCapReached(string contour) =>
        $"Stop the flow; it is past the depth cap of contour '{contour}''s follow, so a resume pauses it again.";

    /// <summary>Names the way out of a flow whose budget, or its chain's, is spent: a resume pauses it again.</summary>
    public static string BudgetReached() =>
        "Stop the flow; its budget is spent, so a resume pauses it again before any judgment.";
}
