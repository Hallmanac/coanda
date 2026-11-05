namespace Coanda.Modules.Software.Repositories;

/// <summary>
/// Where the node keeps its clones and its flow runs' work paths, as the host hands them to the module:
/// a project's clone is <c>&lt;clones&gt;/&lt;project&gt;/repo.git</c> and a flow run's work path is
/// <c>&lt;work&gt;/&lt;project&gt;/&lt;flow run id&gt;</c>, the places a node home written before the module
/// existed already holds them. A project's name must be a plain directory name, so neither path leaves its root.
/// </summary>
public sealed record ClonePaths(string ClonesPath, string WorkPath)
{
    /// <summary>The node's one clone of the project's repository, which every flow run's worktree in the project is cut from.</summary>
    public string ProjectClonePath(string project) =>
        Path.Combine(ClonesPath, RequirePlain(project, "clone of its repository"), "repo.git");

    /// <summary>Where the project's clone is made before it is moved to <see cref="ProjectClonePath"/>, beside it.</summary>
    public string ProjectClonePartialPath(string project) => ProjectClonePath(project) + ".partial";

    /// <summary>Where a flow run's agent steps work, and where its worktree is made.</summary>
    public string FlowRunWorkPath(string project, Guid flowRunId) =>
        Path.Combine(WorkPath, RequirePlain(project, "working directory"), flowRunId.ToString());

    /// <summary>Whether the project's name is a plain directory name, which is what gives its flow runs work paths and a clone.</summary>
    public static bool IsPlain(string project) =>
        project is not ("" or "." or "..")
            && project.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && project.IndexOfAny(['/', '\\']) < 0;

    /// <summary>
    /// The last eight hex characters of <paramref name="id"/>, the way the core refers to a record by hand,
    /// which a parcel's branch name carries.
    /// </summary>
    public static string Short(Guid id) => id.ToString("N")[^8..];

    private static string RequirePlain(string project, string what) =>
        IsPlain(project)
            ? project
            : throw new InvalidOperationException($"Project '{project}' is not a plain directory name, so its flow runs have no {what} under the node home.");
}
