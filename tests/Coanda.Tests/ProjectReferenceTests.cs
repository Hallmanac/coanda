using System.Xml.Linq;
using Coanda.Tests.Sdk;

namespace Coanda.Tests;

/// <summary>
/// The engine never names a module: Core and the SDK reference no Coanda project, the daemon references
/// Core and the SDK alone, a module references the SDK alone, and only the executable's project references
/// a module, which it composes in one file. Module projects are found by folder, so a new one is held
/// without this class changing.
/// </summary>
public sealed class ProjectReferenceTests
{
    private const string ModulePrefix = "Coanda.Modules.";

    /// <summary>The one file in the executable's project that composes the modules the daemon hosts.</summary>
    private const string CompositionFile = "src/Coanda.Cli/ModuleComposition.cs";

    /// <summary>The file holding <c>coa project add</c>'s settings and command, which take a module's settings by the names its describe answer declares.</summary>
    private const string ProjectCommandsFile = "src/Coanda.Cli/Commands/ProjectCommands.cs";

    /// <summary>Each project under <c>src/</c>, by its folder's name, with the names of the folders of the projects it references.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> Projects = new(ReadProjects);

    [Fact]
    public void The_core_references_no_coanda_project() =>
        Assert.Empty(ReferencesOf("Coanda.Core"));

    [Fact]
    public void The_sdk_references_no_coanda_project() =>
        Assert.Empty(ReferencesOf("Coanda.Sdk"));

    [Fact]
    public void The_daemon_references_the_core_and_the_sdk_and_nothing_else() =>
        Assert.Equal(["Coanda.Core", "Coanda.Sdk"], ReferencesOf("Coanda.Daemon").Order(StringComparer.Ordinal));

    [Fact]
    public void Every_module_project_references_the_sdk_and_no_other_coanda_project()
    {
        string[] modules = ModuleProjects();

        Assert.NotEmpty(modules);
        Assert.All(modules, module => Assert.Equal(["Coanda.Sdk"], ReferencesOf(module)));
    }

    [Fact]
    public void No_project_but_the_executables_references_a_module_project()
    {
        string[] modules = ModuleProjects();

        foreach ((string project, string[] references) in Projects.Value)
        {
            if (project != "Coanda.Cli")
            {
                Assert.False(references.Intersect(modules, StringComparer.Ordinal).Any(), $"{project} references a module project.");
            }
        }
    }

    [Fact]
    public void In_the_executables_project_only_the_composition_names_a_module()
    {
        string cli = RepositoryFiles.PathOf("src/Coanda.Cli");
        string[] naming =
        [
            .. Directory.EnumerateFiles(cli, "*.cs", SearchOption.AllDirectories)
                .Where(file => !IsBuildOutput(cli, file) && File.ReadAllText(file).Contains(ModulePrefix, StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(RepositoryFiles.Root, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([CompositionFile], naming);
    }

    [Fact]
    public void The_project_commands_name_no_module_and_no_module_setting()
    {
        string commands = File.ReadAllText(RepositoryFiles.PathOf(ProjectCommandsFile));

        foreach (string named in new[] { "software", "Software", "primary-branch", "PrimaryBranch", "required-checks", "--branch", "--build", "--test" })
        {
            Assert.DoesNotContain(named, commands, StringComparison.Ordinal);
        }
    }

    private static string[] ReferencesOf(string project) =>
        Projects.Value.TryGetValue(project, out string[]? references)
            ? references
            : throw new InvalidOperationException($"No project {project} under src/.");

    private static string[] ModuleProjects() =>
        [.. Projects.Value.Keys.Where(project => project.StartsWith(ModulePrefix, StringComparison.Ordinal))];

    private static Dictionary<string, string[]> ReadProjects()
    {
        Dictionary<string, string[]> projects = new(StringComparer.Ordinal);
        foreach (string folder in Directory.EnumerateDirectories(RepositoryFiles.PathOf("src")))
        {
            string name = Path.GetFileName(folder);
            string file = Path.Combine(folder, $"{name}.csproj");
            if (!File.Exists(file))
            {
                continue;
            }

            projects[name] =
            [
                .. XDocument.Load(file).Descendants("ProjectReference")
                    .Select(reference => (string?)reference.Attribute("Include") ?? "")
                    .Select(include => Path.GetFileName(Path.GetDirectoryName(include.Replace('\\', '/'))) ?? include),
            ];
        }

        return projects;
    }

    private static bool IsBuildOutput(string project, string file)
    {
        string first = Path.GetRelativePath(project, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }
}
