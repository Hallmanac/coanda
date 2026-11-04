using System.Text.Json.Serialization;
using Coanda.Core.Projects.Events;

namespace Coanda.Core.Projects;

/// <summary>
/// A project's record: the repository its software actions act on. It lives in the project's own
/// tenant, one per project. State changes only through <c>Apply</c>; the rules live in
/// <see cref="ProjectDecider"/>. The store snapshots this class inline, so it is also the document
/// a show or list query reads.
/// </summary>
public sealed class Project
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public string Name { get; private set; } = string.Empty;
    [JsonInclude]
    public string Remote { get; private set; } = string.Empty;
    [JsonInclude]
    public string PrimaryBranch { get; private set; } = string.Empty;
    [JsonInclude]
    public string RegisteredBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset RegisteredAt { get; private set; }

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
        Remote = registered.Remote;
        PrimaryBranch = registered.PrimaryBranch;
        RegisteredBy = registered.RegisteredBy;
        RegisteredAt = registered.RegisteredAt;
    }
}
