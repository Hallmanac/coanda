using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;

namespace Coanda.Core.Projects;

/// <summary>
/// The rules for registering a project, for naming the checks its merge waits for, and for switching
/// modules on for it, as pure decisions: the event that records it, or a
/// <see cref="DomainException"/> quoting the rule. The caller has already checked the name the way
/// every project command does, and hands in the record the name already has, if any, and the names of
/// the projects already registered, so a name differing from one of them only in letter case is
/// refused: the node home gives each project a directory named after it, and on a filesystem that
/// ignores case the two would share one, and one clone of a repository. Nothing here
/// parses the remote or contacts it, or reads the build and test commands beyond refusing a blank one.
/// </summary>
public static class ProjectDecider
{
    /// <summary>The rule a command quotes when the project it names has no record.</summary>
    public static string Unregistered(string project) =>
        $"Project '{project}' is not registered; register it with coa project add {project} --repo <url> --branch <name>.";

    public static ProjectRegistered Register(
        string name,
        string remote,
        string primaryBranch,
        Project? existing,
        string registeredBy,
        DateTimeOffset now,
        string? buildCommand = null,
        string? testCommand = null,
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
                $"Project '{trimmed}' is already registered, with the remote {existing.Remote} and the primary branch {existing.PrimaryBranch}.");
        }

        if ((registeredNames ?? []).FirstOrDefault(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase)) is { } differingInCase)
        {
            throw new DomainException(
                $"Project '{trimmed}' differs from the registered project '{differingInCase}' only in letter case; the node keeps each project's clone and work in a directory named after it, and on a filesystem that ignores case the two would share them, so choose another name.");
        }

        if (string.IsNullOrWhiteSpace(remote))
        {
            throw new DomainException($"Project '{trimmed}' needs the remote of its repository; none was given.");
        }

        if (string.IsNullOrWhiteSpace(primaryBranch))
        {
            throw new DomainException($"Project '{trimmed}' needs the primary branch of its repository; none was given.");
        }

        if (buildCommand is not null && string.IsNullOrWhiteSpace(buildCommand))
        {
            throw new DomainException($"Project '{trimmed}' was given a blank build command; name one, or leave it out to run {Project.DefaultBuild}.");
        }

        if (testCommand is not null && string.IsNullOrWhiteSpace(testCommand))
        {
            throw new DomainException($"Project '{trimmed}' was given a blank test command; name one, or leave it out to run {Project.DefaultTest}.");
        }

        return new ProjectRegistered(Ids.New(), trimmed, remote, primaryBranch, registeredBy, now, buildCommand, testCommand);
    }

    /// <summary>
    /// Names the checks a merge of the project's pull requests waits for, replacing the ones it named.
    /// The project must have a record, at least one check must be given, and no check may be blank;
    /// each is recorded exactly as given.
    /// </summary>
    public static ProjectRequiredChecksSet SetRequiredChecks(
        string project, Project? registered, IReadOnlyList<string>? checks, string setBy, DateTimeOffset now)
    {
        if (registered is null)
        {
            throw new DomainException(Unregistered(project));
        }

        if (checks is null || checks.Count == 0)
        {
            throw new DomainException(
                $"Project '{registered.Name}' needs at least one check for a merge to wait for; none was given.");
        }

        // A request read from the wire can carry a null element, whatever the annotations say.
        if (checks.Any(string.IsNullOrWhiteSpace))
        {
            throw new DomainException(
                $"Project '{registered.Name}' was given a blank check name; each check a merge waits for is named by its check run name or status context.");
        }

        return new ProjectRequiredChecksSet(registered.Id, registered.Name, [.. checks], setBy, now);
    }

    /// <summary>
    /// Names the set of modules switched on for the project, replacing the set it held. The project must
    /// have a record and each module must be named by a plain name; a module named twice is recorded
    /// once, and the set is recorded in ordinal order. An empty set switches every module off.
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
                    $"Project '{registered.Name}' was given '{module}' as a module to switch on; a module's name is a plain name (letters, digits, hyphens, and underscores).");
            }
        }

        return new ProjectModulesSet(
            registered.Id, registered.Name, [.. named.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], setBy, now);
    }
}
