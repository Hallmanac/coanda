using System.Reflection;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Daemon.Api;
using Coanda.Daemon.Modules;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using Coanda.Tests.Sdk;
using Coanda.Tests.Sdk.Contract;
using NJsonSchema;

namespace Coanda.Tests.Modules.Software;

/// <summary>
/// The software module's types, flows, and agents are JSON files in its project's definitions folder,
/// one definition per file, beside a <c>module.json</c> the contract's manifest schema accepts, and no
/// C# defines them.
/// </summary>
public sealed class SoftwareModuleDefinitionsTests
{
    /// <summary>The types that once carried the definitions in C#, none of which may come back.</summary>
    private static readonly string[] RetiredTypes = ["ShippedDefinitions", "ShippedDefinition", "ShippedTypes", "ShippedFlows", "ShippedAgents"];

    [Fact]
    public void The_manifest_validates_against_the_contracts_manifest_schema()
    {
        JsonSchema schema = ModuleSchemas.Load("manifest.schema.json");

        string manifest = File.ReadAllText(Path.Combine(SoftwareDefinitions.ProjectPath, ModuleArtifact.ManifestFile));

        Assert.Empty(schema.Validate(manifest));
        Assert.Equal(ModuleApi.Version, SoftwareDefinitions.Manifest.ModuleApiVersion);
    }

    [Fact]
    public void The_manifests_name_is_the_name_the_steps_are_prefixed_with_and_projects_switch_the_module_on_by()
    {
        string name = SoftwareDefinitions.Manifest.Name;

        Assert.Equal(SoftwareSteps.Module, name);
        Assert.All(SoftwareSteps.All, step => Assert.StartsWith($"{name}/", step, StringComparison.Ordinal));
        Assert.All(SoftwareDescribe.Answer.Steps, step => Assert.StartsWith($"{name}/", step.Name, StringComparison.Ordinal));
        Assert.Contains(name, HostedModules.Defaults);
    }

    [Fact]
    public async Task The_definitions_folder_holds_the_two_types_four_flows_and_five_agents_one_per_file_named_for_it()
    {
        ModuleArtifact artifact = await ModuleArtifact.ReadAsync(SoftwareDefinitions.ProjectPath, CancellationToken.None);

        Assert.Equal((SoftwareDefinitions.Manifest.Name, SoftwareDefinitions.Manifest.Version), (artifact.Module, artifact.Version));
        Assert.Equal(
            [
                (DefinitionKind.ParcelType, "idea"), (DefinitionKind.ParcelType, "task"),
                (DefinitionKind.Flow, "build"), (DefinitionKind.Flow, "discovery"), (DefinitionKind.Flow, "merge"), (DefinitionKind.Flow, "review-on-assignment"),
                (DefinitionKind.Agent, "build"), (DefinitionKind.Agent, "discovery"), (DefinitionKind.Agent, "fix"),
                (DefinitionKind.Agent, "reviewer-adversarial"), (DefinitionKind.Agent, "reviewer-conformance"),
            ],
            artifact.Definitions.Select(d => (d.Definition.Kind, d.Definition.Name)));
        Assert.All(artifact.Definitions, d => Assert.Equal($"{d.Definition.Name}.json", Path.GetFileName(d.File)));
        Assert.Equivalent(SoftwareDefinitions.All, artifact.Definitions.Select(d => d.Definition), strict: true);
    }

    [Fact]
    public async Task The_build_copies_the_manifest_and_definitions_beside_the_daemon_where_it_reads_them()
    {
        ModuleArtifact copied = await ModuleArtifact.ReadAsync(DaemonOptions.DefaultSoftwareModulePath, CancellationToken.None);
        ModuleArtifact source = await ModuleArtifact.ReadAsync(SoftwareDefinitions.ProjectPath, CancellationToken.None);

        Assert.Equal((source.Module, source.Version), (copied.Module, copied.Version));
        Assert.Equal(source.Definitions.Select(d => d.Definition), copied.Definitions.Select(d => d.Definition));
    }

    [Fact]
    public void No_csharp_defines_the_modules_definitions()
    {
        Assert.False(Directory.Exists(RepositoryFiles.PathOf("src/Coanda.Core/Software")));
        Type[] everywhere =
        [
            .. typeof(Flow).Assembly.GetTypes(), .. typeof(DaemonHost).Assembly.GetTypes(), .. typeof(SoftwareModule).Assembly.GetTypes(),
            .. typeof(ModuleManifest).Assembly.GetTypes(), .. typeof(Coanda.Cli.DaemonClient).Assembly.GetTypes(), .. Assembly.GetExecutingAssembly().GetTypes(),
        ];
        foreach (string name in RetiredTypes)
        {
            Assert.DoesNotContain(everywhere, type => type.Name == name);
        }

    }
}
