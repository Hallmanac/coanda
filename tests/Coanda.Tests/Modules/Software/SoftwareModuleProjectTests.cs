using System.Reflection;
using System.Xml.Linq;
using Coanda.Core.Flows;
using Coanda.Daemon.Api;
using Coanda.Modules.Software;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Daemon.Modules;
using Coanda.Tests.Sdk;

namespace Coanda.Tests.Modules.Software;

/// <summary>
/// The software module stands on the module SDK alone: its project references no other Coanda project,
/// the code it was carved from is gone from the daemon and the core, and outside the host adapter the
/// daemon reaches it only through the module API's calls.
/// </summary>
public sealed class SoftwareModuleProjectTests
{
    /// <summary>The types that moved into the module, none of which the daemon or the core may hold again.</summary>
    private static readonly string[] MovedTypes =
    [
        "WorktreeStep", "GatesStep", "OpenPrStep", "GatedMergeStep", "RebaseStep", "RecordedHeads", "SoftwareSteps",
        "ProjectClones", "ListedWorktree", "RealPath", "Checkout", "Push", "Rebase", "WorktreeHolders",
        "GitHubClient", "GitHubRepository", "GitHubToken", "GitHubTokenUnavailableException", "GitHubRefusedException",
        "GitHubPullRequest", "GitHubPullRequestState", "GitHubCheckRun", "GitHubCommitStatus", "GitHubMergeMethods", "NoGitHubHandler",
    ];

    /// <summary>The files the moved code lived in, which may not come back under the daemon or the core.</summary>
    private static readonly string[] MovedFiles =
    [
        "src/Coanda.Core/Software/SoftwareSystemSteps.cs",
        "src/Coanda.Daemon/SystemSteps/WorktreeSystemStep.cs",
        "src/Coanda.Daemon/SystemSteps/GatesSystemStep.cs",
        "src/Coanda.Daemon/SystemSteps/OpenPrSystemStep.cs",
        "src/Coanda.Daemon/SystemSteps/GatedMergeSystemStep.cs",
        "src/Coanda.Daemon/SystemSteps/RebaseSystemStep.cs",
        "src/Coanda.Daemon/SystemSteps/RecordedHeads.cs",
        "src/Coanda.Daemon/Repositories",
        "src/Coanda.Daemon/GitHub",
    ];

    [Fact]
    public void The_module_project_references_the_sdk_and_no_other_project()
    {
        XDocument project = XDocument.Load(RepositoryFiles.PathOf("src/Coanda.Modules.Software/Coanda.Modules.Software.csproj"));

        Assert.Equal(
            [@"..\Coanda.Sdk\Coanda.Sdk.csproj"],
            project.Descendants("ProjectReference").Select(reference => (string?)reference.Attribute("Include")));
    }

    [Fact]
    public void The_moved_code_exists_once_in_the_module_and_nowhere_in_the_daemon_or_the_core()
    {
        Assembly module = typeof(SoftwareModule).Assembly;
        Type[] elsewhere = [.. typeof(DaemonHost).Assembly.GetTypes(), .. typeof(Flow).Assembly.GetTypes()];
        foreach (string name in MovedTypes)
        {
            Assert.Single(module.GetTypes(), type => type.Name == name);
            Assert.DoesNotContain(elsewhere, type => type.Name == name);
        }

        foreach (string path in MovedFiles)
        {
            string full = RepositoryFiles.PathOf(path);
            Assert.False(File.Exists(full) || (Directory.Exists(full) && Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories).Any()), $"{path} holds moved code.");
        }
    }

    [Fact]
    public void The_daemon_reaches_the_module_through_the_module_API_calls_and_nothing_else()
    {
        // The interim seam for worktree removal, the start-up sweep, and the worker wait is gone from both sides.
        Assert.DoesNotContain(typeof(SoftwareModule).Assembly.GetTypes(), type => type.Name == "IInterimWorktrees");
        Assert.DoesNotContain(typeof(DaemonHost).Assembly.GetTypes(), type => type.Name == "InterimWorktreeLink");
        Assert.False(File.Exists(RepositoryFiles.PathOf("src/Coanda.Modules.Software/IInterimWorktrees.cs")));

        // The module is built from its options alone, and every public method it has beyond disposal is a call the adapter carries.
        ConstructorInfo constructor = Assert.Single(typeof(SoftwareModule).GetConstructors());
        Assert.Equal([typeof(SoftwareModuleOptions)], constructor.GetParameters().Select(p => p.ParameterType));
        Assert.DoesNotContain(typeof(SoftwareModuleOptions).GetProperties(), p => p.PropertyType.IsInterface && p.PropertyType.Assembly == typeof(SoftwareModule).Assembly);
        IReadOnlyList<string> calls = [.. ModuleHostAdapterTests.CallsOf(typeof(SoftwareModule)).Where(call => call != "dispose")];
        Assert.Equal(ModuleHostAdapterTests.CallsOf(typeof(IModule)).Order(StringComparer.Ordinal), calls.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Five_names_main_shipped_unprefixed_are_read_as_the_modules_and_no_other()
    {
        Assert.Equal(SoftwareSteps.All, SoftwareSteps.Unprefixed.Select(SoftwareSteps.Canonical));
        Assert.Equal(SoftwareSteps.Unprefixed, Coanda.Core.Registry.UnprefixedSteps.Names);
        Assert.Equal(SoftwareSteps.Module, Coanda.Core.Registry.UnprefixedSteps.Module);
        Assert.All(SoftwareSteps.All, name => Assert.Equal(name, SoftwareSteps.Canonical(name)));
        Assert.Equal("deploy", SoftwareSteps.Canonical("deploy"));
        Assert.Equal("cms/publish", SoftwareSteps.Canonical("cms/publish"));
    }
}
