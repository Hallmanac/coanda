using Coanda.Core.Projects.Events;

namespace Coanda.Core.Projects;

/// <summary>
/// The rules for registering a project, as a pure decision: the event that records it, or a
/// <see cref="DomainException"/> quoting the rule. The caller has already checked the name the way
/// every project command does, and hands in the record the name already has, if any. Nothing here
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
        string? testCommand = null)
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
}
