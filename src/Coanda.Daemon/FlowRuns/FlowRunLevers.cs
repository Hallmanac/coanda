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

    /// <summary>
    /// Names the way out of a flow run whose step <paramref name="step"/> refused the answer step <paramref name="from"/>
    /// recorded: a resume performs the step again on that same answer, so it pauses again while what refused it stands.
    /// </summary>
    public static string AnswerReadAgain(string step, string from) =>
        $"Stop the flow run, or change what refused the answer; a resume performs step '{step}' again on the same answer of step '{from}', so it pauses again while that refuses it.";

    /// <summary>
    /// Names the way out of a flow run whose parcel keeps a version of its type <paramref name="type"/> that may not
    /// derive what the answer lists, while the version the project holds now may: no change to the project lets a
    /// resume derive from that parcel, so the way forward is a new parcel, created under the version held now.
    /// </summary>
    public static string TypeVersionKept(string type) =>
        $"Stop the flow run and add a new '{type}' parcel to derive from; the flow run's parcel keeps the earlier version of type '{type}' it was created under, so a resume pauses again.";
}
