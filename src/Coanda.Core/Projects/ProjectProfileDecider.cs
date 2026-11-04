using Coanda.Core.Projects.Events;

namespace Coanda.Core.Projects;

/// <summary>
/// The rules for naming the credential profile a project spawns with, as a pure decision: the event
/// that records it, or a <see cref="DomainException"/> quoting the rule. The caller has already
/// checked the project's name the way every project command does, and hands in the project's record,
/// its current setting, and the profile names the running daemon's configuration holds.
/// </summary>
public static class ProjectProfileDecider
{
    public static ProjectProfileSet Set(
        string project,
        Project? registered,
        ProjectProfile? existing,
        string profile,
        IReadOnlyCollection<string> held,
        string setBy,
        DateTimeOffset now)
    {
        if (registered is null)
        {
            throw new DomainException(ProjectDecider.Unregistered(project));
        }

        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new DomainException($"Project '{project}' needs the name of a credential profile; none was given.");
        }

        // Names are compared as the node configuration compares them: ordinally, after trimming.
        string trimmed = profile.Trim();
        if (!held.Contains(trimmed, StringComparer.Ordinal))
        {
            string holds = held.Count == 0
                ? "holds no credential profiles"
                : $"holds {string.Join(", ", held.Order(StringComparer.Ordinal).Select(n => $"'{n}'"))}";
            throw new DomainException(
                $"The running daemon's node configuration holds no credential profile named '{trimmed}', so project '{project}' cannot name it; the configuration {holds}.");
        }

        return new ProjectProfileSet(existing?.Id ?? Ids.New(), project, trimmed, setBy, now);
    }
}
