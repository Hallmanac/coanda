using System.Text.Json.Serialization;
using Coanda.Core.Projects.Events;

namespace Coanda.Core.Projects;

/// <summary>
/// A project's record: the repository its software system steps act on, the build and test commands
/// its gates run there, which default to <see cref="DefaultBuild"/> and <see cref="DefaultTest"/>,
/// the checks a merge waits for, which are none until they are set, and the modules switched on for it,
/// which are none until they are set. It lives in the project's own
/// tenant, one per project. State changes only through <c>Apply</c>; the rules live in
/// <see cref="ProjectDecider"/>. The store snapshots this class inline, so it is also the document
/// a show or list query reads.
/// </summary>
public sealed class Project
{
    /// <summary>The build command a record that names none runs.</summary>
    public const string DefaultBuild = "dotnet build";

    /// <summary>The test command a record that names none runs.</summary>
    public const string DefaultTest = "dotnet test";

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

    /// <summary>The build command the registration named, or null when it named none.</summary>
    [JsonInclude]
    public string? BuildCommand { get; private set; }

    /// <summary>The test command the registration named, or null when it named none.</summary>
    [JsonInclude]
    public string? TestCommand { get; private set; }

    /// <summary>
    /// The checks a merge of the project's pull requests waits for, by check run name or commit status
    /// context, exactly as they were set; empty until they are.
    /// </summary>
    [JsonInclude]
    public IReadOnlyList<string> RequiredChecks { get; private set; } = [];

    /// <summary>
    /// The modules switched on for the project, by name, in ordinal order; empty until they are set. A
    /// module acts in a project only while it is switched on for it.
    /// </summary>
    [JsonInclude]
    public IReadOnlyList<string> Modules { get; private set; } = [];

    /// <summary>The build command the gates run: the one named, or <see cref="DefaultBuild"/>.</summary>
    [JsonIgnore]
    public string Build => BuildCommand ?? DefaultBuild;

    /// <summary>The test command the gates run: the one named, or <see cref="DefaultTest"/>.</summary>
    [JsonIgnore]
    public string Test => TestCommand ?? DefaultTest;

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
        BuildCommand = registered.BuildCommand;
        TestCommand = registered.TestCommand;
    }

    public void Apply(ProjectRequiredChecksSet set) => RequiredChecks = [.. set.Checks];

    public void Apply(ProjectModulesSet set) => Modules = [.. set.Modules];

    /// <summary>Whether the module <paramref name="module"/> is switched on for the project.</summary>
    public bool IsSwitchedOn(string module) => Modules.Contains(module, StringComparer.Ordinal);
}
