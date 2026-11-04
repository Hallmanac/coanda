using System.Text.Json.Serialization;
using Coanda.Core.Projects.Events;

namespace Coanda.Core.Projects;

/// <summary>
/// The credential profile a project's agent steps spawn with on this node: a node-local setting, one
/// per project, kept in the node tenant. State changes only through <c>Apply</c>; the rules live in
/// <see cref="ProjectProfileDecider"/>. The store snapshots this class inline.
/// </summary>
public sealed class ProjectProfile
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public string Project { get; private set; } = string.Empty;
    [JsonInclude]
    public string Profile { get; private set; } = string.Empty;
    [JsonInclude]
    public string SetBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset SetAt { get; private set; }

    public static ProjectProfile Create(ProjectProfileSet set)
    {
        var setting = new ProjectProfile();
        setting.Apply(set);
        return setting;
    }

    public void Apply(ProjectProfileSet set)
    {
        Id = set.SettingId;
        Project = set.Project;
        Profile = set.Profile;
        SetBy = set.SetBy;
        SetAt = set.SetAt;
    }
}
