using Coanda.Core;
using Coanda.Core.Registry;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The projects this node watches: those whose watches it hands its modules, and in which it accepts a
/// module's external events. The host supplies them (<see cref="Api.DaemonOptions.WatchedProjects"/>), from
/// the node configuration's <c>watchedProjects</c>. Null names, as when the configuration says nothing about
/// watching, watch every project; a list watches the projects it names, an entry naming a project when,
/// trimmed, it equals the project's name without regard to letter case.
/// </summary>
public sealed class WatchedProjects
{
    /// <summary>Every project is watched, as on a node whose configuration says nothing about watching.</summary>
    public static readonly WatchedProjects All = new(null);

    private readonly HashSet<string>? names;

    public WatchedProjects(IReadOnlyList<string>? names) =>
        this.names = names is null
            ? null
            : new HashSet<string>(names.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this node watches the project named <paramref name="project"/>.</summary>
    public bool Watches(string project) =>
        names is null || names.Contains(project.Trim());

    /// <summary>
    /// Refuses a Module principal's external event in the project <paramref name="tenant"/> unless this node
    /// watches it; every other principal passes untouched.
    /// </summary>
    public void RequireWatchedForModule(Principal principal, string tenant)
    {
        if (principal.Role == Role.Module && !Watches(tenant))
        {
            throw new DomainException(
                $"This node does not watch project '{tenant}', so module '{principal.Module}' cannot record an external event in it here; \"watchedProjects\" in the node configuration decides which projects a node watches.");
        }
    }
}
