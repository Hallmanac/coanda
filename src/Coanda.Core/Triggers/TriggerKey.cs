using Coanda.Core.Definitions;

namespace Coanda.Core.Triggers;

/// <summary>
/// How a trigger, and one firing of it, is named in what it records: the id of the principal that
/// creates its parcel and starts its flow run, and the origin of a parcel it creates. A trigger is
/// named by its scope, by its module in module scope, its project in project scope, or the word
/// <c>owner</c> in owner scope, and by its name in lower case, since definition names compare without
/// regard to case. A firing adds its cause, the external event's id or the ended flow run's id. None of
/// these is a definition's id or version number, which differ between versions and, for owner-scope and
/// module-scope definitions, between nodes, and none is a value of an event field, so every node names
/// one firing alike and nothing an event carries reaches the ledger through the name.
/// </summary>
public static class TriggerKey
{
    /// <summary>The trigger's own name: <c>trigger:project:&lt;project&gt;/&lt;name&gt;</c>, <c>trigger:owner/&lt;name&gt;</c>, or <c>trigger:module:&lt;module&gt;/&lt;name&gt;</c>.</summary>
    public static string Of(Definition definition) => definition.Scope switch
    {
        DefinitionScope.Project => $"trigger:project:{definition.Project}/{Folded(definition.Name)}",
        DefinitionScope.Owner => $"trigger:owner/{Folded(definition.Name)}",
        DefinitionScope.Module => $"trigger:module:{definition.Module}/{Folded(definition.Name)}",
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Scope, "A definition is of project, owner, or module scope."),
    };

    /// <summary>The trigger's firing on the external event <paramref name="eventId"/>, the id it was recorded under on the node that recorded it.</summary>
    public static string ForEvent(string trigger, Guid eventId) => $"{trigger}:event:{eventId}";

    /// <summary>The trigger's firing on the end of the flow run <paramref name="flowRunId"/>.</summary>
    public static string ForFlowRunEnd(string trigger, Guid flowRunId) => $"{trigger}:flow-run:{flowRunId}";

    private static string Folded(string name) => name.ToLowerInvariant();
}
