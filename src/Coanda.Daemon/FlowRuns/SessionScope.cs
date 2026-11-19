using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Registry;

namespace Coanda.Daemon.FlowRuns;

/// <summary>
/// What narrows a session principal, one resolved from an agent step's session token, beyond the
/// registry's role check: it reads, moves parcels, sets fields, and drafts project definitions only in
/// the project its session is bound to, and it records on its own flow run alone
/// (<see cref="FlowRunService"/> holds it to that). The test is the principal's session binding, never
/// its role: the daemon's own loop principals are Agent-role principals with no session, and act in
/// every project. Every principal without a session passes each check untouched.
/// </summary>
public static class SessionScope
{
    /// <summary>
    /// Refuses a session principal in the project <paramref name="tenant"/>, a name
    /// <c>Tenants.RequireProject</c> has already returned, unless its session is bound to it. The refusal
    /// begins with the session's flow-run rule (<see cref="BoundRule"/>), so a write that reads the flow
    /// run before it records is refused the same way whichever check reaches it first.
    /// </summary>
    public static void RequireBoundProject(Principal principal, string tenant)
    {
        if (principal.Session is { } bound && !IsBoundProject(bound, tenant))
        {
            throw new DomainException(
                $"{BoundRule(bound)}; project '{tenant}' is not project '{bound.Project}', and a session reads, moves parcels, sets fields, and drafts project definitions only in the project its session is bound to.");
        }
    }

    /// <summary>
    /// Refuses a session principal a draft of <paramref name="name"/> in any scope but project scope:
    /// owner scope reaches every project its owner holds, and a session drafts project definitions only,
    /// in the project its session is bound to.
    /// </summary>
    public static void RequireProjectScope(Principal principal, DefinitionScope scope, string name)
    {
        if (principal.Session is { } bound && scope != DefinitionScope.Project)
        {
            throw new DomainException(
                $"{BoundRule(bound)}; '{name.Trim()}' is drafted in {scope} scope, and a session drafts definitions only in project scope, in project '{bound.Project}'.");
        }
    }

    /// <summary>
    /// Whether <paramref name="principal"/> may act in the project named <paramref name="project"/>: any
    /// principal without a session may, and a session principal may in the project its session is bound to.
    /// </summary>
    public static bool MayActIn(Principal principal, string project) =>
        principal.Session is not { } bound || IsBoundProject(bound, project);

    /// <summary>The rule every refusal of a session principal begins with: the flow run, project, and launch it is bound to.</summary>
    public static string BoundRule(SessionBinding bound) =>
        $"A session's principal records only on flow run {Ids.Short(bound.FlowRunId)} in project '{bound.Project}', the flow run it is bound to, while its launch {Ids.Short(bound.LaunchId)} runs";

    private static bool IsBoundProject(SessionBinding bound, string project) =>
        string.Equals(bound.Project, project, StringComparison.Ordinal);
}
