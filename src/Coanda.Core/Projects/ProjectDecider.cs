using Coanda.Core.Projects.Events;

namespace Coanda.Core.Projects;

/// <summary>
/// The rules for registering a project, as a pure decision: the event that records it, or a
/// <see cref="DomainException"/> quoting the rule. The caller has already checked the name the way
/// every project command does, and hands in the record the name already has, if any. Nothing here
/// parses the remote or contacts it.
/// </summary>
public static class ProjectDecider
{
    public static ProjectRegistered Register(
        string name, string remote, string primaryBranch, Project? existing, string registeredBy, DateTimeOffset now)
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

        if (string.IsNullOrWhiteSpace(remote))
        {
            throw new DomainException($"Project '{trimmed}' needs the remote of its repository; none was given.");
        }

        if (string.IsNullOrWhiteSpace(primaryBranch))
        {
            throw new DomainException($"Project '{trimmed}' needs the primary branch of its repository; none was given.");
        }

        return new ProjectRegistered(Ids.New(), trimmed, remote, primaryBranch, registeredBy, now);
    }
}
