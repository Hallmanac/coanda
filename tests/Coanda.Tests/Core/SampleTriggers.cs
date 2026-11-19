using Coanda.Core.Triggers;

namespace Coanda.Tests.Core;

/// <summary>Trigger definitions a test drafts, imports, or breaks.</summary>
public static class SampleTriggers
{
    /// <summary>A trigger that creates an issue parcel from a module's event and starts triage on it.</summary>
    public static TriggerDefinition IssueOpened { get; } = new()
    {
        Name = "issue-opened",
        EventKind = "tracker/issue-opened",
        Filters = new Dictionary<string, string>(StringComparer.Ordinal) { ["repo"] = "coanda", ["assignee"] = "pat" },
        Target = new TriggerTarget { CreateParcel = new CreateParcelTarget { ParcelType = "issue", TitleField = "title", Flow = "triage" } },
        HourlyCap = 10,
    };

    /// <summary>A trigger that starts a flow on the parcel a module's event names.</summary>
    public static TriggerDefinition ReviewRequested { get; } = new()
    {
        Name = "review-requested",
        EventKind = "tracker/review-requested",
        Filters = new Dictionary<string, string>(StringComparer.Ordinal) { ["reviewer"] = "pat" },
        Target = new TriggerTarget { EventParcel = new EventParcelTarget { Flow = "review" } },
    };

    /// <summary>A trigger that creates an evaluation parcel when a build flow run ends with a pull request opened.</summary>
    public static TriggerDefinition EvaluateBuild { get; } = new()
    {
        Name = "evaluate-build",
        EventKind = TriggerEvent.FlowRunEndedKind,
        Filters = new Dictionary<string, string>(StringComparer.Ordinal) { ["flow"] = "build", ["outcome"] = "pr-opened" },
        Target = new TriggerTarget { CreateParcel = new CreateParcelTarget { ParcelType = "evaluation", TitleField = "flow", Flow = "judge" } },
        HourlyCap = 5,
    };

    public static IReadOnlyDictionary<string, string> Filters(params (string Field, string Value)[] filters) =>
        filters.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);
}
