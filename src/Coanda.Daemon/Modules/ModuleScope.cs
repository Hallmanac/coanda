using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.Projects;
using Fisher;

namespace Coanda.Daemon.Modules;

/// <summary>
/// What narrows a Module principal beyond the registry's role check: it acts only in a project its
/// module is switched on for (<see cref="Project.Modules"/>), and the external event kinds it records
/// carry its own module's name. Every other principal passes each check untouched.
/// </summary>
public static class ModuleScope
{
    /// <summary>
    /// Refuses a Module principal in the project <paramref name="tenant"/>, a name
    /// <c>Tenants.RequireProject</c> has already returned, unless its module is switched on for it. An
    /// unregistered project has no module switched on.
    /// </summary>
    public static async ValueTask RequireSwitchedOnAsync(
        IDocumentStore store, Principal principal, string tenant, CancellationToken cancellationToken)
    {
        if (principal.Role != Role.Module)
        {
            return;
        }

        Project? project = await ProjectService.FindAsync(store, tenant, cancellationToken);
        if (!IsSwitchedOn(principal, project))
        {
            throw new DomainException(
                $"Module '{principal.Module}' is not switched on for project '{tenant}'; a module acts only in a project it is switched on for.");
        }
    }

    /// <summary>
    /// Whether <paramref name="principal"/> may act in <paramref name="project"/>: any principal but a
    /// Module principal may, and a Module principal may when the project has a record and its module is
    /// switched on for it.
    /// </summary>
    public static bool IsSwitchedOn(Principal principal, Project? project) =>
        principal.Role != Role.Module
        || (principal.Module is { } module && project is not null && project.IsSwitchedOn(module));

    /// <summary>
    /// Refuses a Module principal's external event kind <paramref name="kind"/> unless it has the form
    /// <c>&lt;module&gt;/&lt;name&gt;</c> with its own module's name before the slash.
    /// </summary>
    public static void RequireOwnKind(Principal principal, string kind)
    {
        if (principal.Role == Role.Module
            && (principal.Module is null || !string.Equals(ModuleNames.PrefixOf(kind), principal.Module, StringComparison.Ordinal)))
        {
            throw new DomainException(
                $"Module '{principal.Module}' records external events of kinds it prefixes with its own name, '{principal.Module}/<name>'; '{kind}' is not one.");
        }
    }
}
