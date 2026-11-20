using Coanda.Core.Definitions;

namespace Coanda.Core.Triggers;

/// <summary>
/// How a trigger, and one firing of it, is named in what it records: the id of the principal that
/// creates its parcel and starts its flow run, and the origin of a parcel it creates. A trigger is
/// named by its scope, by its module in module scope, its project in project scope, or the word
/// <c>owner</c> in owner scope, and by its name in lower case, since definition names compare without
/// regard to case. A firing adds its cause, the external event's id, the ended flow run's id, or the id of
/// the pause it fired on. None of these is a definition's id or version number, which differ between
/// versions and, for owner-scope and module-scope definitions, between nodes, and none is a value of an
/// event field, so every node names one firing alike and nothing an event carries reaches the ledger through the name.
/// </summary>
public static class TriggerKey
{
    /// <summary>What every trigger's name in what it records begins with.</summary>
    public const string Prefix = "trigger:";

    private static readonly string[] Causes = [":event:", ":flow-run:", ":pause:"];

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

    /// <summary>The trigger's firing on the pause recorded under the event id <paramref name="pauseEventId"/>, on the node that started the paused flow run.</summary>
    public static string ForPause(string trigger, Guid pauseEventId) => $"{trigger}:pause:{pauseEventId}";

    /// <summary>
    /// The trigger name in <paramref name="origin"/>, a parcel's origin in <paramref name="project"/>, when a trigger's
    /// firing created it, whatever the trigger's scope; null for any other origin. A project-scope trigger's name follows
    /// the project's own, which the project's parcels all carry, so a name holding a slash is read whole.
    /// </summary>
    public static string? NameOf(string origin, string project)
    {
        if (!origin.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string? named = Causes.Select(cause => CutCause(origin, cause)).FirstOrDefault(cut => cut is not null);
        if (named is null)
        {
            return null;
        }

        string scoped = named[Prefix.Length..];
        string projectScope = $"project:{project}/";
        if (scoped.StartsWith(projectScope, StringComparison.Ordinal))
        {
            return scoped[projectScope.Length..];
        }

        int slash = scoped.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && (scoped.StartsWith("owner/", StringComparison.Ordinal) || scoped.StartsWith("module:", StringComparison.Ordinal))
            ? scoped[(slash + 1)..]
            : null;
    }

    /// <summary><paramref name="origin"/> without the cause that ends it, <paramref name="cause"/> and a flow run's or an event's id, or null when it does not end so.</summary>
    private static string? CutCause(string origin, string cause)
    {
        int at = origin.LastIndexOf(cause, StringComparison.Ordinal);
        return at > 0 && Guid.TryParse(origin.AsSpan(at + cause.Length), out _)
            ? origin[..at]
            : null;
    }

    private static string Folded(string name) => name.ToLowerInvariant();
}
