namespace Coanda.Modules.Software;

/// <summary>
/// The names of the software module's replicated settings in a project, and of the task field it owns
/// that names the repo a task works in. <see cref="SoftwareDescribe.Settings"/> declares the settings.
/// </summary>
public static class SoftwareSettings
{
    /// <summary>Each repo's primary branch: an object naming, for a repo's name, its branch.</summary>
    public const string PrimaryBranch = "primary-branch";

    /// <summary>The command line the gates step runs first.</summary>
    public const string Build = "build";

    /// <summary>The command line the gates step runs once the build passes.</summary>
    public const string Test = "test";

    /// <summary>
    /// The checks a merge of a pull request waits for, by check run name or commit status context, each
    /// until it has succeeded, whether or not it has been reported yet. Unset, the merge waits on every
    /// check reported on the head, and holds a head with none reported for
    /// <see cref="Steps.GatedMergeStep.SettleBound"/> after it was recorded before merging it. Naming
    /// checks is how a project makes the merge wait for a check that has not been reported by the time
    /// every reported check has finished, such as one created only after another finishes or one from a
    /// slower integration.
    /// </summary>
    public const string RequiredChecks = "required-checks";

    /// <summary>The build command a project that sets none runs.</summary>
    public const string DefaultBuild = "dotnet build";

    /// <summary>The test command a project that sets none runs.</summary>
    public const string DefaultTest = "dotnet test";

    /// <summary>The task field, owned by the module, naming the repo the task works in.</summary>
    public const string ReposField = "repos";

    /// <summary>
    /// The file inside a credential profile selected for the module that holds the GitHub token its steps
    /// act with in the project. A profile is a directory directly inside the module's area of the node home.
    /// </summary>
    public const string GitHubTokenFile = "github-token";

    /// <summary>The command that gives the setting <paramref name="name"/> the value <paramref name="example"/> in <paramref name="project"/>.</summary>
    public static string SetCommand(string project, string name, string example) =>
        $"coa project module set {project} {SoftwareSteps.Module} {name} {example}";
}
