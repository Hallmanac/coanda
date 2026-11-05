namespace Coanda.Sdk.Repositories;

/// <summary>
/// Where a node home keeps the core's bare clone of each repo of a project:
/// <c>projects/&lt;project&gt;/repos/&lt;repo&gt;</c> under the home. The core makes and holds the clones,
/// and a module finds a repo's clone by this rule, so the rule is written here once and both read it.
/// </summary>
public static class RepoLayout
{
    /// <summary>The directory under the node home holding one directory per project.</summary>
    public static string ProjectsPath(string homePath) => Path.Combine(homePath, "projects");

    /// <summary>The directory holding the project's clones, one per repo.</summary>
    public static string ReposPath(string homePath, string project) =>
        Path.Combine(ProjectsPath(homePath), RequirePlain(project, "project"), "repos");

    /// <summary>
    /// The core's bare clone of <paramref name="repo"/> in <paramref name="project"/>. Both must be plain
    /// directory names (<see cref="IsPlain"/>), so the path never leaves the project's <c>repos</c> directory;
    /// a repo's name always is one, and a project's is checked here.
    /// </summary>
    public static string ClonePath(string homePath, string project, string repo) =>
        Path.Combine(ReposPath(homePath, project), RequirePlain(repo, "repo"));

    /// <summary>Whether <paramref name="name"/> is one plain directory name on every platform: not empty, not <c>.</c> or <c>..</c>, and holding no separator or character a file name cannot.</summary>
    public static bool IsPlain(string name) =>
        name is not ("" or "." or "..")
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && name.IndexOfAny(['/', '\\']) < 0;

    private static string RequirePlain(string name, string what) =>
        IsPlain(name)
            ? name
            : throw new ArgumentException($"The {what} name '{name}' is not a plain directory name, so it has no clone under the node home.", nameof(name));
}
