using System.Text.Json.Serialization;
using Coanda.Core.Projects.Events;

namespace Coanda.Core.Projects;

/// <summary>
/// A project's record: its name, its repos, and the modules switched on for it, which are none until
/// they are set. It holds nothing a module keeps for the project: the software module's primary branch,
/// build and test commands, and required checks are that module's replicated settings. It lives in the
/// project's own tenant, one per project. State changes only through <c>Apply</c>; the rules live in
/// <see cref="ProjectDecider"/>. The store snapshots this class inline, so it is also the document
/// a show or list query reads.
/// </summary>
public sealed class Project
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public string Name { get; private set; } = string.Empty;

    /// <summary>The project's repos, in the order they were recorded; registration records the first.</summary>
    [JsonInclude]
    public IReadOnlyList<ProjectRepo> Repos { get; private set; } = [];
    [JsonInclude]
    public string RegisteredBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset RegisteredAt { get; private set; }

    /// <summary>
    /// The modules switched on for the project, by name, in ordinal order; empty until they are set. A
    /// module acts in a project only while it is switched on for it.
    /// </summary>
    [JsonInclude]
    public IReadOnlyList<string> Modules { get; private set; } = [];

    public static Project Create(ProjectRegistered registered)
    {
        var project = new Project();
        project.Apply(registered);
        return project;
    }

    public void Apply(ProjectRegistered registered)
    {
        Id = registered.ProjectId;
        Name = registered.Name;
        Repos = [.. registered.ReposRecorded];
        RegisteredBy = registered.RegisteredBy;
        RegisteredAt = registered.RegisteredAt;
    }

    public void Apply(ProjectModulesSet set) => Modules = [.. set.Modules];

    /// <summary>Whether the module <paramref name="module"/> is switched on for the project.</summary>
    public bool IsSwitchedOn(string module) => Modules.Contains(module, StringComparer.Ordinal);
}
