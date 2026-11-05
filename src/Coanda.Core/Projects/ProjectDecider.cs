using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;

namespace Coanda.Core.Projects;

/// <summary>
/// The rules for registering a project and for switching modules on for it, as pure decisions: the
/// event that records it, or a <see cref="DomainException"/> quoting the rule. The caller has already
/// checked the name the way every project command does, and hands in the record the name already has,
/// if any, and the names of the projects already registered, so a name differing from one of them only
/// in letter case is refused: the node home gives each project a directory named after it, and on a
/// filesystem that ignores case the two would share one, and one clone of a repository. Nothing here
/// parses the remote or contacts it, and nothing here knows a module's settings.
/// </summary>
public static class ProjectDecider
{
    /// <summary>The rule a command quotes when the project it names has no record.</summary>
    public static string Unregistered(string project) =>
        $"Project '{project}' is not registered; register it with coa project add {project} --repo <url> --branch <name>.";

    /// <summary>
    /// Registers a project with one repo, at <paramref name="remote"/>, named by
    /// <see cref="ProjectRepo.NameOf"/>. A name and a remote are required.
    /// </summary>
    public static ProjectRegistered Register(
        string name,
        string remote,
        Project? existing,
        string registeredBy,
        DateTimeOffset now,
        IEnumerable<string>? registeredNames = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A project name is required; none was given.");
        }

        string trimmed = name.Trim();
        if (existing is not null)
        {
            throw new DomainException(
                $"Project '{trimmed}' is already registered, with the repos {string.Join(", ", existing.Repos.Select(r => $"{r.Name} ({r.Remote})"))}.");
        }

        if ((registeredNames ?? []).FirstOrDefault(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase)) is { } differingInCase)
        {
            throw new DomainException(
                $"Project '{trimmed}' differs from the registered project '{differingInCase}' only in letter case; the node keeps each project's clone and work in a directory named after it, and on a filesystem that ignores case the two would share them, so choose another name.");
        }

        if (string.IsNullOrWhiteSpace(remote))
        {
            throw new DomainException($"Project '{trimmed}' needs the remote of a repo; none was given.");
        }

        return new ProjectRegistered(Ids.New(), trimmed, [ProjectRepo.FromRemote(remote)], registeredBy, now);
    }

    /// <summary>
    /// Names the set of modules switched on for the project, replacing the set it held. The project must
    /// have a record and each module must be named by a module's name (lowercase letters and digits in
    /// runs joined by single hyphens); a module named twice is recorded once, and the set is recorded in
    /// ordinal order. An empty set switches every module off.
    /// </summary>
    public static ProjectModulesSet SetModules(
        string project, Project? registered, IReadOnlyList<string>? modules, string setBy, DateTimeOffset now)
    {
        if (registered is null)
        {
            throw new DomainException(Unregistered(project));
        }

        IReadOnlyList<string> named = modules ?? [];

        // A request read from the wire can carry a null element, whatever the annotations say.
        foreach (string module in named)
        {
            if (!ModuleNames.IsModuleName(module))
            {
                throw new DomainException(
                    $"Project '{registered.Name}' was given '{module}' as a module to switch on; a module's name is {ModuleNames.Rule}.");
            }
        }

        return new ProjectModulesSet(
            registered.Id, registered.Name, [.. named.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], setBy, now);
    }
}
