using System.Reflection;
using System.Xml.Linq;
using Coanda.Daemon.Api;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Contract;

namespace Coanda.Tests.Sdk;

/// <summary>
/// The module SDK stands on its own: it references no other Coanda project, the daemon references
/// it, the command runner and git wrapper live in it and nowhere else, and its contract types are
/// sealed but for the bases of a document with several kinds.
/// </summary>
public sealed class SdkProjectTests
{
    private static readonly string[] MovedTypes =
    [
        "ICommandRunner", "CommandLine", "CommandResult", "ProcessCommandRunner", "NoCommandRunner",
        "OutputText", "CommandNotStartedException", "Git", "GitCommandException", "Durations",
    ];

    [Fact]
    public void The_sdk_project_references_no_other_project()
    {
        XDocument project = XDocument.Load(RepositoryFiles.PathOf("src/Coanda.Sdk/Coanda.Sdk.csproj"));
        Assert.Empty(project.Descendants("ProjectReference").Select(reference => (string?)reference.Attribute("Include")));
    }

    [Fact]
    public void The_daemon_references_the_sdk()
    {
        XDocument project = XDocument.Load(RepositoryFiles.PathOf("src/Coanda.Daemon/Coanda.Daemon.csproj"));
        Assert.Contains(
            @"..\Coanda.Sdk\Coanda.Sdk.csproj",
            project.Descendants("ProjectReference").Select(reference => (string?)reference.Attribute("Include")));
    }

    [Fact]
    public void The_command_runner_and_git_wrapper_exist_once_in_the_sdk()
    {
        Assembly sdk = typeof(ICommandRunner).Assembly;
        foreach (string name in MovedTypes)
        {
            Assert.Single(sdk.GetTypes(), type => type.Name == name);
            Assert.DoesNotContain(typeof(DaemonHost).Assembly.GetTypes(), type => type.Name == name);
            Assert.Empty(Directory.GetFiles(RepositoryFiles.PathOf("src/Coanda.Daemon"), $"{name}.cs", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void Every_contract_type_is_sealed_but_the_base_of_a_document_with_several_kinds()
    {
        Type[] contract = [.. typeof(ModuleApi).Assembly.GetTypes().Where(type => type.Namespace == typeof(ModuleApi).Namespace && type.IsClass && !type.IsAbstract && !type.IsSealed && !IsStatic(type))];
        Assert.Empty(contract);

        Type[] bases = [.. typeof(ModuleApi).Assembly.GetTypes().Where(type => type.Namespace == typeof(ModuleApi).Namespace && type.IsAbstract && !IsStatic(type))];
        Assert.Equal([typeof(DispatchAnswer), typeof(FeedEvent), typeof(StatusAnswer)], bases.OrderBy(type => type.Name, StringComparer.Ordinal));
        Assert.All(bases, kindsOf =>
        {
            Type[] kinds = [.. kindsOf.Assembly.GetTypes().Where(type => type.BaseType == kindsOf)];
            Assert.NotEmpty(kinds);
            Assert.All(kinds, kind =>
            {
                Assert.True(kind.IsSealed, $"{kind.FullName} is not sealed.");
                Assert.Equal(kindsOf, kind.DeclaringType);
            });
            // A record's copy constructor is protected and only copies an existing kind, so it opens nothing.
            ConstructorInfo[] constructors = [.. kindsOf.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(constructor => constructor.GetParameters() is not [{ } only] || only.ParameterType != kindsOf)];
            Assert.All(constructors, constructor => Assert.True(constructor.IsPrivate, $"{kindsOf.Name} has a constructor that is not private."));
        });
    }

    private static bool IsStatic(Type type) => type.IsAbstract && type.IsSealed;
}
