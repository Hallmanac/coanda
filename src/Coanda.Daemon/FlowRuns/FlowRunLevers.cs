namespace Coanda.Daemon.FlowRuns;

/// <summary>The lever a pause carries when nothing more specific names what a person acts on.</summary>
public static class FlowRunLevers
{
    /// <summary>Names the step a resume performs again.</summary>
    public static string PerformAgain(string step) => $"Resume the flow run to perform step '{step}' again.";

    /// <summary>Names the way out of a flow run that has spawned as many agents as its flow allows: a resume cannot spawn another.</summary>
    public static string SpawnCapReached(string flow) =>
        $"Stop the flow run; flow '{flow}' allows no more spawns, so a resume cannot perform another agent step.";

    /// <summary>Names the way out of a flow run whose current step no transition's visit cap allows another visit to: a resume pauses it again.</summary>
    public static string VisitCapsSpent(string step) =>
        $"Stop the flow run; every transition out of step '{step}' has spent its visit cap, so a resume pauses it again.";

    /// <summary>Names the way out of a flow run deeper in its chain than its flow's follow allows: a resume pauses it again.</summary>
    public static string DepthCapReached(string flow) =>
        $"Stop the flow run; it is past the depth cap of flow '{flow}''s follow, so a resume pauses it again.";

    /// <summary>Names the way out of a flow run whose budget, or its chain's, is spent: a resume pauses it again.</summary>
    public static string BudgetReached() =>
        "Stop the flow run; its budget is spent, so a resume pauses it again before any agent step.";
}
